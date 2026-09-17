using Cvolo.Compiler.Tooling;
using Cvolo.LanguageServer.Core.Backend;
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
        out BackendDiagnosticLocation mapped)
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
