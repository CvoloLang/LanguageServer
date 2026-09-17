using Cvolo.LanguageServer.Core.Backend;

namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// The coherent request context for a semantic request: the document state
/// captured at its publication plus the project and current project snapshot at
/// capture time. Supersedes the LSP-1 <c>RequestDocumentContext</c>; the two
/// contracts must not coexist.
/// </summary>
internal readonly record struct SemanticRequestContext(
    DocumentUri Uri,
    DocumentSessionId SessionId,
    DocumentVersion Version,
    DocumentState Document,
    BackendProject Project,
    BackendSnapshot CurrentProjectSnapshot);
