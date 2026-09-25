using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.SignatureHelp;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Logging;
using System.Collections.Concurrent;
using CoreTextSpan = Cvolo.LanguageServer.Core.Diagnostics.TextSpan;

namespace Cvolo.LanguageServer.Cvolo;

/// <summary>
/// Adapts the Cvolo compiler tooling to the core backend boundary. Owns the
/// Cvolo workspace, per-project sessions (one immutable ProjectSnapshot per
/// project) and the serialization gate that keeps snapshot advancement safe
/// across concurrent requests for the same project.
/// </summary>
internal sealed class CvoloLanguageBackend(
    IReadOnlyList<string> workspaceFolders,
    string? fallbackRoot,
    ICoreLogger? logger = null,
    IReadOnlyList<string>? libraryPaths = null) : ILanguageBackend
{
    private readonly ICoreLogger _logger = logger ?? new NullCoreLogger();
    private readonly string? _fallbackRoot = fallbackRoot;
    private readonly IReadOnlyList<string> _libraryPaths = libraryPaths ?? Array.Empty<string>();
    private readonly ConcurrentDictionary<string, CvoloProjectSession> _sessions = new();

    public BackendProject? OpenProject(DocumentUri document)
    {
        var boundary = SelectBoundary(document.LocalPath);
        ProjectDiscoveryResult discovery = ProjectDiscovery.FindProject(document.LocalPath, boundary);

        if (discovery.Status is ProjectDiscoveryStatus.NoProject or ProjectDiscoveryStatus.LooseWorkspace
            && TryFindOwningSession(document.LocalPath, out var owner))
        {
            return owner;
        }

        if (discovery.Status == ProjectDiscoveryStatus.AmbiguousProject)
        {
            _logger.Write(CoreLogLevel.Warning, $"Ambiguous project for '{document}': '{discovery.ProjectDirectory}' contains more than one .cvlproj.");
            return null;
        }

        if (discovery.ProjectDirectory is null)
        {
            _logger.Write(CoreLogLevel.Warning, $"No filesystem root could be selected for '{document}'.");
            return null;
        }

        var root = Path.GetFullPath(discovery.ProjectDirectory);
        var sessionKey = discovery.Status == ProjectDiscoveryStatus.LooseWorkspace
            ? "loose:" + root
            : "project:" + root;

        try
        {
            return _sessions.GetOrAdd(
                sessionKey,
                _ => CreateSession(root, discovery.Status == ProjectDiscoveryStatus.LooseWorkspace));
        }
        catch (Exception ex)
        {
            var mode = discovery.Status == ProjectDiscoveryStatus.LooseWorkspace ? "loose workspace" : "Cvolo project";
            _logger.Write(CoreLogLevel.Error, $"Opening {mode} '{root}' failed: {ex.Message}");
            return null;
        }
    }

    private bool TryFindOwningSession(string path, out CvoloProjectSession session)
    {
        foreach (var candidate in _sessions.Values)
        {
            if (candidate.Project.TryGetDocumentId(path, out _))
            {
                session = candidate;
                return true;
            }
        }

        session = null!;
        return false;
    }

    public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
    {
        var session = (CvoloProjectSession)project;
        if (session.Project.TryGetDocumentId(document.LocalPath, out DocumentId documentId))
        {
            handle = new CvoloDocumentHandle(documentId);
            return true;
        }

        _logger.Write(CoreLogLevel.Error, $"Document '{document}' is not part of project '{session.Project.ProjectPath}'.");
        handle = null!;
        return false;
    }

    public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
    {
        var session = (CvoloProjectSession)project;
        var document = (CvoloDocumentHandle)handle;

        lock (session.Gate)
        {
            ProjectSnapshot next = session.Current.WithDocument(document.DocumentId, SourceText.From(text));
            session.Advance(next);
            return new ToolingBackendSnapshot(next, session.Generation);
        }
    }

    public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
    {
        var session = (CvoloProjectSession)project;
        var document = (CvoloDocumentHandle)handle;

        lock (session.Gate)
        {
            // The disk baseline can change while a document is open (save, rename edits applied by
            // the client, external tools). Read it at close so removing the editor overlay never
            // resurrects the project text captured when the language server first opened it.
            SourceText baseline = session.ReadDiskBaseline(document.DocumentId);
            ProjectSnapshot next = session.Current.WithDocument(document.DocumentId, baseline);
            session.Advance(next);
            return new ToolingBackendSnapshot(next, session.Generation);
        }
    }

    public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
    {
        var session = (CvoloProjectSession)project;
        lock (session.Gate)
        {
            return new ToolingBackendSnapshot(session.Current, session.Generation);
        }
    }

    public BackendSnapshot SynchronizeClosedDocuments(BackendProject project, IReadOnlyList<BackendDocumentHandle> openDocuments)
    {
        var session = (CvoloProjectSession)project;
        var openIds = openDocuments
            .OfType<CvoloDocumentHandle>()
            .Select(handle => handle.DocumentId)
            .ToHashSet();

        lock (session.Gate)
        {
            ProjectSnapshot next = session.Current;
            var changed = false;

            foreach (DocumentId documentId in next.DocumentIds)
            {
                if (openIds.Contains(documentId))
                    continue;

                DocumentSnapshot document = next.GetDocument(documentId);
                if (!File.Exists(document.FilePath))
                    continue;

                string diskText;
                try
                {
                    diskText = File.ReadAllText(document.FilePath);
                }
                catch (IOException ex)
                {
                    _logger.Write(CoreLogLevel.Debug, $"Closed-document refresh skipped for '{document.FilePath}': {ex.Message}");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.Write(CoreLogLevel.Debug, $"Closed-document refresh skipped for '{document.FilePath}': {ex.Message}");
                    continue;
                }

                session.SetDiskBaseline(documentId, SourceText.From(diskText));
                if (string.Equals(document.Text.ToString(), diskText, StringComparison.Ordinal))
                    continue;

                next = next.WithDocument(documentId, SourceText.From(diskText));
                changed = true;
            }

            if (changed)
                session.Advance(next);

            return new ToolingBackendSnapshot(session.Current, session.Generation);
        }
    }

    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        var session = (CvoloProjectSession)project;
        var toolingSnapshot = (ToolingBackendSnapshot)snapshot;
        lock (session.Gate)
        {
            return session.Generation == toolingSnapshot.Generation;
        }
    }

    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var texts = new Dictionary<DocumentUri, string>();
        var diagnostics = new List<BackendDiagnostic>();

        foreach (BackendDocumentHandle handle in targets)
        {
            var documentId = ((CvoloDocumentHandle)handle).DocumentId;
            DocumentSnapshot document = toolingSnapshot.GetDocument(documentId);
            DocumentUri documentUri = ToDocumentUri(document.FilePath);
            texts[documentUri] = document.Text.ToString();

            foreach (Diagnostic diagnostic in document.GetDiagnostics())
            {
                if (!TryMapLocation(toolingSnapshot, diagnostic.Location, texts, out BackendDiagnosticLocation? location))
                {
                    _logger.Write(CoreLogLevel.Warning, $"Skipping diagnostic '{diagnostic.Id}' with an unresolved primary location in '{document.FilePath}'.");
                    continue;
                }

                var related = new List<BackendDiagnosticLocation>(diagnostic.RelatedLocations.Count);
                foreach (DiagnosticLocation relatedLocation in diagnostic.RelatedLocations)
                {
                    if (TryMapLocation(toolingSnapshot, relatedLocation, texts, out BackendDiagnosticLocation? mapped))
                    {
                        related.Add(mapped);
                    }
                    else
                    {
                        _logger.Write(CoreLogLevel.Debug, $"Skipping unresolved related location of diagnostic '{diagnostic.Id}'.");
                    }
                }

                diagnostics.Add(new BackendDiagnostic(
                    MapSeverity(diagnostic.Severity),
                    diagnostic.Id,
                    diagnostic.Message,
                    location,
                    related));
            }
        }

        return new BackendDiagnosticRun(texts, diagnostics);
    }

    private static bool TryMapLocation(
        ProjectSnapshot snapshot,
        DiagnosticLocation location,
        Dictionary<DocumentUri, string> texts,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BackendDiagnosticLocation? mapped)
    {
        if (snapshot.TryGetDocument(location.DocumentId, out DocumentSnapshot? document)
            && DocumentUri.TryCreate(document.FilePath, out DocumentUri uri))
        {
            texts.TryAdd(uri, document.Text.ToString());
            mapped = new BackendDiagnosticLocation(
                uri,
                new CoreTextSpan(location.Span.Start, location.Span.Length),
                location.Message);
            return true;
        }

        mapped = null!;
        return false;
    }

    private static DocumentUri ToDocumentUri(string filePath)
    {
        return DocumentUri.TryCreate(filePath, out DocumentUri uri) ? uri : DocumentUri.Create(filePath);
    }

    private static BackendDiagnosticSeverity MapSeverity(DiagnosticSeverity severity)
    {
        return severity switch
        {
            DiagnosticSeverity.Error => BackendDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => BackendDiagnosticSeverity.Warning,
            DiagnosticSeverity.Info => BackendDiagnosticSeverity.Info,
            DiagnosticSeverity.Hint => BackendDiagnosticSeverity.Hint,
            _ => BackendDiagnosticSeverity.Warning,
        };
    }

    private CvoloProjectSession CreateSession(string semanticRoot, bool looseWorkspace)
    {
        // The workspace builds the same semantic universe as the compiler (project sources,
        // standard library, ProjectReference sources and package/.cvlib API units).
        var workspace = CvoloWorkspace.Create();
        var project = looseWorkspace
            ? workspace.OpenProject(semanticRoot, _libraryPaths)
            : workspace.OpenProject(semanticRoot);
        _logger.Write(
            CoreLogLevel.Info,
            looseWorkspace
                ? $"Opened loose Cvolo workspace '{semanticRoot}' with {_libraryPaths.Count} configured library path(s)."
                : $"Opened Cvolo project '{semanticRoot}' from .cvlproj package/project metadata.");
        return new CvoloProjectSession(workspace, project);
    }

    /// <summary>
    /// The most specific workspace folder containing the document wins. A
    /// document outside every folder falls back to the single fallback root
    /// (rootUri, then rootPath); only when neither exists is discovery
    /// unbounded.
    /// </summary>
    private string? SelectBoundary(string documentPath)
    {
        string? best = null;
        foreach (var folder in workspaceFolders)
        {
            if (folder is null || !IsContaining(folder, documentPath))
            {
                continue;
            }

            if (best is null || folder.Length > best.Length)
            {
                best = folder;
            }
        }

        return best ?? _fallbackRoot;
    }

    private static bool IsContaining(string root, string documentPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(normalizedRoot, documentPath, comparison))
        {
            return true;
        }

        return documentPath.Length > normalizedRoot.Length
            && documentPath.StartsWith(normalizedRoot, comparison)
            && (documentPath[normalizedRoot.Length] == Path.DirectorySeparatorChar || documentPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar);
    }

    public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
        {
            // The handle does not belong to the supplied snapshot. Refuse the
            // operation rather than fabricate a result for a document we cannot
            // see; the handler degrades the failure to a null response (§20).
            throw new InvalidOperationException("The completion document is not present in the captured snapshot.");
        }

        int textLength = toolingDocument.Text.Length;
        if (position < 0 || position > textLength)
        {
            // Never clamp or invent a replacement span for an impossible offset.
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The completion position is outside the captured document text.");
        }

        CompletionResult result = toolingDocument.GetCompletions(position);
        var toolingSpan = result.ReplacementRange;

        if (!IsValidReplacementSpan(toolingSpan.Start, toolingSpan.Length, position, textLength))
        {
            // Reject malformed Tooling output rather than repairing it: a bad
            // span must never become an unsafe TextEdit (§14, §17).
            throw new InvalidOperationException(
                $"The completion result returned an invalid replacement span " +
                $"(start={toolingSpan.Start}, length={toolingSpan.Length}) for position {position} over {textLength} code unit(s).");
        }

        var items = new List<BackendCompletionItem>(result.Candidates.Count);
        foreach (CompletionCandidate candidate in result.Candidates)
        {
            items.Add(new BackendCompletionItem(
                candidate.Label,
                candidate.PlainInsertText,
                MapCompletionKind(candidate.Kind),
                candidate.Detail,
                MapInsertionPlan(candidate.InsertionPlan),
                candidate.ItemId is { } itemId ? new CvoloCompletionResolveHandle(toolingSnapshot, itemId) : null,
                MapResolvableFields(candidate.ResolvableFields)));
        }

        return new BackendCompletionResult(
            new CoreTextSpan(toolingSpan.Start, toolingSpan.Length),
            items);
    }

    private static BackendCompletionInsertionPlan? MapInsertionPlan(CompletionInsertionPlan? plan)
    {
        if (plan is null)
            return null;

        var segments = new List<BackendCompletionInsertSegment>(plan.SnippetSegments.Count);
        foreach (CompletionInsertSegment segment in plan.SnippetSegments)
        {
            switch (segment)
            {
                case CompletionLiteral literal:
                    segments.Add(new BackendCompletionLiteral(literal.Text));
                    break;
                case CompletionPlaceholder placeholder:
                    segments.Add(new BackendCompletionPlaceholder(placeholder.DefaultText));
                    break;
                case CompletionFinalCursor:
                    segments.Add(new BackendCompletionFinalCursor());
                    break;
                default:
                    throw new InvalidOperationException($"Unknown completion insertion segment '{segment.GetType().Name}'.");
            }
        }

        return new BackendCompletionInsertionPlan(segments);
    }

    private static BackendCompletionResolvableFields MapResolvableFields(CompletionResolvableFields fields)
    {
        var result = BackendCompletionResolvableFields.None;
        if (fields.HasFlag(CompletionResolvableFields.Detail))
            result |= BackendCompletionResolvableFields.Detail;
        if (fields.HasFlag(CompletionResolvableFields.Documentation))
            result |= BackendCompletionResolvableFields.Documentation;
        return result;
    }

    public BackendCompletionResolvedInfo? ResolveCompletion(BackendSnapshot snapshot, BackendCompletionResolveHandle handle)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (handle is not CvoloCompletionResolveHandle completionHandle || !ReferenceEquals(completionHandle.Snapshot, toolingSnapshot))
        {
            // A foreign or mismatched completion handle must never resolve against the wrong
            // snapshot; return no result rather than an unrelated item (§28, §30).
            return null;
        }

        CompletionResolvedInfo? resolved = toolingSnapshot.ResolveCompletion(completionHandle.ItemId);
        if (resolved is null)
            return null;

        return new BackendCompletionResolvedInfo(resolved.Detail, resolved.Documentation);
    }

    public BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
            throw new InvalidOperationException("The signature-help document is not present in the captured snapshot.");

        if (position < 0 || position > toolingDocument.Text.Length)
            throw new ArgumentOutOfRangeException(nameof(position), position, "The signature-help position is outside the captured document text.");

        SignatureHelpInfo? result = toolingDocument.GetSignatureHelp(position);
        if (result is null)
            return null;

        var signatures = result.Signatures
            .Select(signature => new BackendSignatureCandidate(
                signature.Label,
                signature.Documentation,
                signature.Parameters.Select(parameter => new BackendSignatureParameter(
                    new BackendSignatureLabelSpan(parameter.LabelSpan.Start, parameter.LabelSpan.Length),
                    parameter.Documentation)).ToArray(),
                signature.ActiveParameter))
            .ToArray();

        return new BackendSignatureHelpResult(signatures, result.ActiveSignature);
    }

    public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
        {
            // The handle does not belong to the supplied snapshot. Refuse rather
            // than fabricate a symbol for a document we cannot see; the handler
            // degrades the failure to a null response (§23).
            throw new InvalidOperationException("The navigation document is not present in the captured snapshot.");
        }

        if (position < 0 || position > toolingDocument.Text.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The navigation position is outside the captured document text.");
        }

        SymbolLookupResult? result = toolingDocument.GetSymbolAtPosition(position);
        if (result is null)
        {
            return null;
        }

        return new BackendSymbolInfo(
            new CvoloSymbolHandle(toolingSnapshot, result.SymbolId),
            new CoreTextSpan(result.SubjectSpan.Start, result.SubjectSpan.Length),
            result.Name,
            MapSymbolKind(result.Kind),
            result.DisplayText,
            result.Documentation,
            MapNativeInterop(result.NativeInterop));
    }

    public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (symbol is not CvoloSymbolHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
        {
            // A foreign or mismatched symbol handle must never resolve against the
            // wrong snapshot; return no result rather than an unrelated symbol (§15).
            return EmptyDefinitions;
        }

        IReadOnlyList<SymbolDefinition> definitions = toolingSnapshot.GetDefinitions(handle.SymbolId);
        if (definitions.Count == 0)
        {
            return EmptyDefinitions;
        }

        var texts = new Dictionary<DocumentUri, string>();
        var targets = new List<BackendDefinitionTarget>(definitions.Count);
        foreach (SymbolDefinition definition in definitions)
        {
            if (!toolingSnapshot.TryGetDocument(definition.DocumentId, out DocumentSnapshot? targetDocument))
            {
                continue;
            }

            DocumentUri uri = ToDocumentUri(targetDocument.FilePath);
            texts.TryAdd(uri, targetDocument.Text.ToString());
            targets.Add(new BackendDefinitionTarget(
                uri,
                new CoreTextSpan(definition.Range.Start, definition.Range.Length),
                new CoreTextSpan(definition.SelectionSpan.Start, definition.SelectionSpan.Length)));
        }

        return new BackendDefinitionResult(texts, targets);
    }

    public BackendReferenceResult GetReferences(BackendSnapshot snapshot, BackendSymbolHandle symbol, bool includeDeclaration)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (symbol is not CvoloSymbolHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
            return new BackendReferenceResult(new Dictionary<DocumentUri, string>(), []);

        IReadOnlyList<SymbolReference> references = toolingSnapshot.GetReferences(handle.SymbolId, includeDeclaration);
        var texts = new Dictionary<DocumentUri, string>();
        var locations = new List<BackendReferenceLocation>(references.Count);
        foreach (SymbolReference reference in references)
        {
            if (!toolingSnapshot.TryGetDocument(reference.DocumentId, out DocumentSnapshot? targetDocument))
                continue;

            DocumentUri uri = ToDocumentUri(targetDocument.FilePath);
            texts.TryAdd(uri, targetDocument.Text.ToString());
            locations.Add(new BackendReferenceLocation(
                uri,
                new CoreTextSpan(reference.Span.Start, reference.Span.Length),
                reference.IsDeclaration));
        }

        return new BackendReferenceResult(texts, locations);
    }

    public BackendRenamePreparation? PrepareRename(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;
        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
            throw new InvalidOperationException("The rename document is not present in the captured snapshot.");

        RenamePreparation? preparation = toolingDocument.PrepareRename(position);
        if (preparation is null)
            return null;

        return new BackendRenamePreparation(
            new CvoloSymbolHandle(toolingSnapshot, preparation.SymbolId),
            new CoreTextSpan(preparation.SubjectSpan.Start, preparation.SubjectSpan.Length),
            preparation.Placeholder);
    }

    public BackendRenameResult RenameSymbol(BackendSnapshot snapshot, BackendSymbolHandle symbol, string newName)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (symbol is not CvoloSymbolHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
            return new BackendRenameFailure("The selected symbol does not belong to the captured project snapshot.");

        RenameResult result = toolingSnapshot.RenameSymbol(handle.SymbolId, newName);
        if (result is RenameFailure failure)
            return new BackendRenameFailure(failure.Message);

        var success = (RenameSuccess)result;
        var texts = new Dictionary<DocumentUri, string>();
        var edits = new List<BackendRenameEdit>(success.Edits.Count);
        foreach (RenameEdit edit in success.Edits)
        {
            if (!toolingSnapshot.TryGetDocument(edit.DocumentId, out DocumentSnapshot? targetDocument))
                return new BackendRenameFailure("Rename produced an edit outside the captured project snapshot.");

            DocumentUri uri = ToDocumentUri(targetDocument.FilePath);
            texts.TryAdd(uri, targetDocument.Text.ToString());
            edits.Add(new BackendRenameEdit(uri, new CoreTextSpan(edit.Span.Start, edit.Span.Length), edit.NewText));
        }

        return new BackendRenameSuccess(texts, edits);
    }

    public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
        {
            throw new InvalidOperationException("The document-symbol document is not present in the captured snapshot.");
        }

        IReadOnlyList<DocumentSymbolInfo> symbols = toolingDocument.GetDocumentSymbols();
        var mapped = new List<BackendDocumentSymbol>(symbols.Count);
        foreach (DocumentSymbolInfo symbol in symbols)
        {
            mapped.Add(MapDocumentSymbol(symbol));
        }

        return mapped;
    }

    private static BackendDocumentSymbol MapDocumentSymbol(DocumentSymbolInfo symbol)
    {
        var children = new List<BackendDocumentSymbol>(symbol.Children.Count);
        foreach (DocumentSymbolInfo child in symbol.Children)
        {
            children.Add(MapDocumentSymbol(child));
        }

        return new BackendDocumentSymbol(
            symbol.Name,
            symbol.Detail,
            MapSymbolKind(symbol.Kind),
            new CoreTextSpan(symbol.Range.Start, symbol.Range.Length),
            new CoreTextSpan(symbol.SelectionSpan.Start, symbol.SelectionSpan.Length),
            children);
    }

    public BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
        {
            throw new InvalidOperationException("The semantic-token document is not present in the captured snapshot.");
        }

        IReadOnlyList<SemanticTokenInfo> tokens = toolingDocument.GetSemanticTokens();
        var mapped = new List<BackendSemanticToken>(tokens.Count);
        foreach (SemanticTokenInfo token in tokens)
        {
            mapped.Add(new BackendSemanticToken(
                new CoreTextSpan(token.Span.Start, token.Span.Length),
                MapSymbolKind(token.Kind),
                MapTokenModifiers(token.Modifiers)));
        }

        return new BackendSemanticTokenResult(mapped);
    }

    private static BackendSemanticTokenModifiers MapTokenModifiers(SemanticTokenModifiers modifiers)
    {
        var result = BackendSemanticTokenModifiers.None;
        if (modifiers.HasFlag(SemanticTokenModifiers.Declaration))
            result |= BackendSemanticTokenModifiers.Declaration;
        if (modifiers.HasFlag(SemanticTokenModifiers.Readonly))
            result |= BackendSemanticTokenModifiers.Readonly;
        if (modifiers.HasFlag(SemanticTokenModifiers.Static))
            result |= BackendSemanticTokenModifiers.Static;
        return result;
    }

    private static readonly BackendDefinitionResult EmptyDefinitions =
        new(new Dictionary<DocumentUri, string>(), []);

    private static BackendNativeInteropMetadata? MapNativeInterop(NativeInteropMetadata? metadata)
    {
        if (metadata is null || metadata.Kind == NativeInteropKind.None)
            return null;

        var kind = metadata.Kind switch
        {
            NativeInteropKind.NativeDelegate => BackendNativeInteropKind.NativeDelegate,
            NativeInteropKind.RawUnion => BackendNativeInteropKind.RawUnion,
            NativeInteropKind.ForeignGlobal => BackendNativeInteropKind.ForeignGlobal,
            _ => throw new ArgumentOutOfRangeException(nameof(metadata), metadata.Kind, "Unknown native interop kind."),
        };

        return new BackendNativeInteropMetadata(
            kind,
            metadata.CallingConvention,
            metadata.ImportName,
            metadata.LibraryName,
            metadata.WinPath,
            metadata.LinuxPath,
            metadata.MacPath);
    }

    private static BackendSymbolKind MapSymbolKind(ToolingSymbolKind kind)
    {
        return kind switch
        {
            ToolingSymbolKind.Namespace => BackendSymbolKind.Namespace,
            ToolingSymbolKind.Module => BackendSymbolKind.Module,
            ToolingSymbolKind.Struct => BackendSymbolKind.Struct,
            ToolingSymbolKind.Union => BackendSymbolKind.Union,
            ToolingSymbolKind.Enum => BackendSymbolKind.Enum,
            ToolingSymbolKind.EnumMember => BackendSymbolKind.EnumMember,
            ToolingSymbolKind.Interface => BackendSymbolKind.Interface,
            ToolingSymbolKind.Protocol => BackendSymbolKind.Protocol,
            ToolingSymbolKind.Delegate => BackendSymbolKind.Delegate,
            ToolingSymbolKind.TypeAlias => BackendSymbolKind.TypeAlias,
            ToolingSymbolKind.TypeParameter => BackendSymbolKind.TypeParameter,
            ToolingSymbolKind.Function => BackendSymbolKind.Function,
            ToolingSymbolKind.Method => BackendSymbolKind.Method,
            ToolingSymbolKind.ExtensionMethod => BackendSymbolKind.ExtensionMethod,
            ToolingSymbolKind.Constructor => BackendSymbolKind.Constructor,
            ToolingSymbolKind.Destructor => BackendSymbolKind.Destructor,
            ToolingSymbolKind.Field => BackendSymbolKind.Field,
            ToolingSymbolKind.Parameter => BackendSymbolKind.Parameter,
            ToolingSymbolKind.Local => BackendSymbolKind.Local,
            ToolingSymbolKind.Global => BackendSymbolKind.Global,
            ToolingSymbolKind.Constant => BackendSymbolKind.Constant,
            ToolingSymbolKind.Operator => BackendSymbolKind.Operator,
            ToolingSymbolKind.OtherType => BackendSymbolKind.OtherType,
            _ => BackendSymbolKind.Unknown,
        };
    }

    /// <summary>
    /// Validates a Tooling replacement span against the captured text and cursor:
    /// it must be non-negative, lie within the text, and contain the request
    /// offset (<c>Start &lt;= position &lt;= End</c>, §17). Exposed internally so
    /// malformed spans can be covered deterministically without faking Tooling.
    /// </summary>
    internal static bool IsValidReplacementSpan(int start, int length, int position, int textLength)
    {
        return start >= 0
            && length >= 0
            && start + length <= textLength
            && start <= position
            && position <= start + length;
    }

    private static BackendCompletionKind MapCompletionKind(CompletionKind kind)
    {
        return kind switch
        {
            CompletionKind.Local => BackendCompletionKind.Local,
            CompletionKind.Parameter => BackendCompletionKind.Parameter,
            CompletionKind.Global => BackendCompletionKind.Global,
            CompletionKind.Function => BackendCompletionKind.Function,
            CompletionKind.Method => BackendCompletionKind.Method,
            CompletionKind.Type => BackendCompletionKind.Type,
            CompletionKind.Namespace => BackendCompletionKind.Namespace,
            CompletionKind.StructField => BackendCompletionKind.StructField,
            CompletionKind.UnionVariant => BackendCompletionKind.UnionVariant,
            CompletionKind.EnumVariant => BackendCompletionKind.EnumMember,
            CompletionKind.EnumMetadata => BackendCompletionKind.EnumMetadata,
            CompletionKind.ArrayLength => BackendCompletionKind.ArrayLength,
            CompletionKind.Keyword => BackendCompletionKind.Keyword,
            _ => BackendCompletionKind.OtherType,
        };
    }
}

/// <summary>
/// One live project session: a Cvolo project plus its current immutable snapshot
/// and the adapter-owned generation that identifies snapshot advancements. The
/// generation is language-server-internal and never added to the tooling.
/// </summary>
internal sealed class CvoloProjectSession(CvoloWorkspace workspace, CvoloProject project) : BackendProject
{
    public CvoloWorkspace Workspace { get; } = workspace;

    public CvoloProject Project { get; } = project;

    public ProjectSnapshot Baseline => Project.InitialSnapshot;

    public ProjectSnapshot Current { get; private set; } = project.InitialSnapshot;

    public long Generation { get; private set; }

    private readonly Dictionary<DocumentId, SourceText> _diskBaselines = project.InitialSnapshot.Documents
        .ToDictionary(pair => pair.Key, pair => pair.Value.Text);

    public SourceText ReadDiskBaseline(DocumentId documentId)
    {
        DocumentSnapshot document = Current.GetDocument(documentId);
        if (File.Exists(document.FilePath))
        {
            try
            {
                var source = SourceText.From(File.ReadAllText(document.FilePath));
                _diskBaselines[documentId] = source;
                return source;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return _diskBaselines.TryGetValue(documentId, out SourceText? baseline)
            ? baseline
            : Project.InitialSnapshot.GetDocument(documentId).Text;
    }

    public void SetDiskBaseline(DocumentId documentId, SourceText source)
    {
        _diskBaselines[documentId] = source;
    }

    /// <summary>
    /// Serializes advancement of <see cref="Current"/> for this project.
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>
    /// Publishes <paramref name="snapshot"/> as the new current snapshot and
    /// increments the generation. Callers must hold <see cref="Gate"/>.
    /// </summary>
    public void Advance(ProjectSnapshot snapshot)
    {
        Current = snapshot;
        Generation++;
    }
}

/// <summary>
/// Opaque document handle backed by the tooling's session-local DocumentId.
/// </summary>
internal sealed class CvoloDocumentHandle(DocumentId documentId) : BackendDocumentHandle
{
    public DocumentId DocumentId { get; } = documentId;
}

internal sealed class ToolingBackendSnapshot(ProjectSnapshot snapshot, long generation) : BackendSnapshot
{
    public ProjectSnapshot Snapshot { get; } = snapshot;

    public long Generation { get; } = generation;
}

/// <summary>
/// Opaque symbol handle backed by the tooling's snapshot-scoped SymbolId plus the
/// tooling snapshot it was resolved from, so a handle can never resolve against a
/// different snapshot.
/// </summary>
internal sealed class CvoloSymbolHandle(ProjectSnapshot snapshot, SymbolId symbolId) : BackendSymbolHandle
{
    public ProjectSnapshot Snapshot { get; } = snapshot;

    public SymbolId SymbolId { get; } = symbolId;
}

/// <summary>
/// Opaque completion-resolve handle backed by the tooling's snapshot-scoped CompletionItemId
/// plus the tooling snapshot it was minted from, so a handle can never resolve against a
/// different snapshot.
/// </summary>
internal sealed class CvoloCompletionResolveHandle(ProjectSnapshot snapshot, CompletionItemId itemId) : BackendCompletionResolveHandle
{
    public ProjectSnapshot Snapshot { get; } = snapshot;

    public CompletionItemId ItemId { get; } = itemId;
}