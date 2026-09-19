using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
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
internal sealed class CvoloLanguageBackend(IReadOnlyList<string> workspaceFolders, string? fallbackRoot, ICoreLogger? logger = null) : ILanguageBackend
{
    private readonly ICoreLogger _logger = logger ?? new NullCoreLogger();
    private readonly string? _fallbackRoot = fallbackRoot;
    private readonly ConcurrentDictionary<string, CvoloProjectSession> _sessions = new();

    public BackendProject? OpenProject(DocumentUri document)
    {
        var boundary = SelectBoundary(document.LocalPath);
        ProjectDiscoveryResult discovery = ProjectDiscovery.FindProject(document.LocalPath, boundary);
        if (discovery.Status == ProjectDiscoveryStatus.NoProject)
        {
            _logger.Write(CoreLogLevel.Warning, $"No .cvlproj found for '{document}'.");
            return null;
        }

        if (discovery.Status == ProjectDiscoveryStatus.AmbiguousProject)
        {
            _logger.Write(CoreLogLevel.Warning, $"Ambiguous project for '{document}': '{discovery.ProjectDirectory}' contains more than one .cvlproj.");
            return null;
        }

        try
        {
            return _sessions.GetOrAdd(discovery.ProjectDirectory!, CreateSession);
        }
        catch (Exception ex)
        {
            _logger.Write(CoreLogLevel.Error, $"Opening Cvolo project '{discovery.ProjectDirectory}' failed: {ex.Message}");
            return null;
        }
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
            DocumentSnapshot baseline = session.Baseline.GetDocument(document.DocumentId);
            ProjectSnapshot next = session.Current.WithDocument(document.DocumentId, baseline.Text);
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

    private CvoloProjectSession CreateSession(string projectDirectory)
    {
        var workspace = CvoloWorkspace.Create();
        var project = workspace.OpenProject(projectDirectory);
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
            items.Add(new BackendCompletionItem(candidate.Label, candidate.InsertText, MapCompletionKind(candidate.Kind), candidate.IsSnippet));
        }

        return new BackendCompletionResult(
            new CoreTextSpan(toolingSpan.Start, toolingSpan.Length),
            items);
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
