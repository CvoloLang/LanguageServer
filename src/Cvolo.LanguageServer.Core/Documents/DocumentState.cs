using Cvolo.LanguageServer.Core.Backend;

namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Immutable snapshot of one open document plus the backend snapshot it was
/// advanced against, so text and compiler-side state always move together.
/// </summary>
internal sealed record DocumentState(
    DocumentUri Uri,
    DocumentSessionId SessionId,
    DocumentVersion Version,
    string LanguageId,
    string Text,
    BackendDocumentHandle BackendDocument,
    BackendSnapshot BackendSnapshot);