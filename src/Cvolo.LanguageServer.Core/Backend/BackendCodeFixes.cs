using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

internal abstract class BackendCodeFixHandle
{
}

internal sealed record BackendCodeFixInfo(
    BackendCodeFixHandle Handle,
    string Title,
    IReadOnlyList<BackendDiagnostic> Diagnostics);

internal sealed record BackendCodeFixResult(IReadOnlyList<BackendCodeFixInfo> Fixes);

internal sealed record BackendCodeFixEdit(DocumentUri Document, TextSpan Span, string NewText);

internal abstract record BackendCodeFixResolution;

internal sealed record BackendCodeFixSuccess(
    IReadOnlyDictionary<DocumentUri, string> DocumentTexts,
    IReadOnlyList<BackendCodeFixEdit> Edits) : BackendCodeFixResolution;

internal sealed record BackendCodeFixFailure(string Message) : BackendCodeFixResolution;
