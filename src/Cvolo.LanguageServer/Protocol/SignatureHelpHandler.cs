using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/signatureHelp handler. All callable resolution stays compiler-owned; this layer only
/// maps positions, enforces snapshot freshness, and translates protocol-neutral DTOs to LSP shapes.
/// </summary>
internal sealed class SignatureHelpHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public async Task<SignatureHelpResponse?> SignatureHelp(SignatureHelpParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position)
            return null;

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
            return null;

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
            return null;

        BackendSignatureHelpResult? result;
        try
        {
            result = await Task.Run(() => Store.GetSignatureHelp(context, offset), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[signatureHelp] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(context) || result is null)
            return null;

        return new SignatureHelpResponse(
            result.Signatures.Select(signature => new SignatureInformationPayload(
                signature.Label,
                signature.Parameters.Select(parameter => new ParameterInformationPayload(parameter.Label)).ToArray(),
                signature.Documentation)).ToArray(),
            result.ActiveSignature,
            result.ActiveParameter);
    }
}
