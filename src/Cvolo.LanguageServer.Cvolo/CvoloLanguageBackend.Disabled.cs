using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Logging;

namespace Cvolo.LanguageServer.Cvolo;

internal sealed class CvoloLanguageBackend(IReadOnlyList<string> workspaceFolders, string? fallbackRoot, ICoreLogger? logger = null) : ILanguageBackend
{
    private readonly ICoreLogger _logger = logger ?? new NullCoreLogger();

    public IReadOnlyList<string> WorkspaceFolders { get; } = workspaceFolders;

    public string? FallbackRoot { get; } = fallbackRoot;

    public BackendProject? OpenProject(DocumentUri document)
    {
        _logger.Write(CoreLogLevel.Warning, "Cvolo compiler tooling is unavailable; document synchronization is running without a compiler backend.");
        return null;
    }

    public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
    {
        handle = null!;
        return false;
    }

    public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        return false;
    }

    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }
}
