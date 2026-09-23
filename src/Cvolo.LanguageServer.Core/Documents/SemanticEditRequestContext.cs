using Cvolo.LanguageServer.Core.Backend;

namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>Captured open-document state used to version and validate a multi-document semantic edit.</summary>
internal sealed record OpenDocumentEditState(
    DocumentUri Uri,
    DocumentSessionId SessionId,
    DocumentVersion Version,
    BackendDocumentHandle Document);

/// <summary>
/// One atomic capture of a semantic request plus every open document in the same project publication.
/// </summary>
internal sealed record SemanticEditRequestContext(
    SemanticRequestContext Semantic,
    IReadOnlyDictionary<DocumentUri, OpenDocumentEditState> OpenDocuments);
