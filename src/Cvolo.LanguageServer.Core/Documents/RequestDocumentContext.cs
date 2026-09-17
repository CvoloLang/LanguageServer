using Cvolo.LanguageServer.Core.Backend;

namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Coherent point-in-time capture of an open document for a request: the
/// document state and the backend snapshot it was published with. Later
/// LSP-2 features compare this against the live store to detect stale
/// results; nothing consumes it yet.
/// </summary>
internal readonly record struct RequestDocumentContext(
    DocumentUri Uri,
    DocumentSessionId SessionId,
    DocumentVersion Version,
    DocumentState Document,
    BackendSnapshot BackendSnapshot);