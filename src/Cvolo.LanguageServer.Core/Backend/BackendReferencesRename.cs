using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

internal sealed record BackendReferenceLocation(DocumentUri Document, TextSpan Span, bool IsDeclaration);

internal sealed record BackendReferenceResult(
    IReadOnlyDictionary<DocumentUri, string> DocumentTexts,
    IReadOnlyList<BackendReferenceLocation> Locations);

internal sealed record BackendRenamePreparation(
    BackendSymbolHandle Symbol,
    TextSpan SubjectSpan,
    string Placeholder);

internal sealed record BackendRenameEdit(DocumentUri Document, TextSpan Span, string NewText);

internal abstract record BackendRenameResult;

internal sealed record BackendRenameSuccess(
    IReadOnlyDictionary<DocumentUri, string> DocumentTexts,
    IReadOnlyList<BackendRenameEdit> Edits) : BackendRenameResult;

internal sealed record BackendRenameFailure(string Message) : BackendRenameResult;
