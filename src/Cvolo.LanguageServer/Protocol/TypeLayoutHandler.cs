using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// <c>cvolo/typeLayout</c> request. Serves the complete object layout of one type so the editor can
/// open its read-only layout view. Every number is the compiler's own: the client may format the
/// table, but it may not derive an offset, a size, an alignment or a padding run, and the compiler
/// has no second layout table for the editor to disagree with (§19, §20, §22, §57).
/// </summary>
internal sealed class TypeLayoutHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("cvolo/typeLayout", UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeLayoutResponse?> TypeLayout(TypeLayoutParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position)
        {
            logger.Debug("[typeLayout] request without a textDocument or position; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[typeLayout] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[typeLayout] '{documentUri}' is not open; no layout.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[typeLayout] the position does not map to the captured text of '{documentUri}'.");
            return null;
        }

        BackendTypeLayoutInspection? layout;
        try
        {
            layout = await Task.Run(() => Store.GetTypeLayoutAtPosition(context, offset), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[typeLayout] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[typeLayout] discarded stale result for '{documentUri}'.");
            return null;
        }

        return layout is null ? null : Map(layout);
    }

    private static TypeLayoutResponse Map(BackendTypeLayoutInspection layout)
    {
        var members = new TypeLayoutMemberResponse[layout.Members.Count];
        for (int i = 0; i < members.Length; i++)
        {
            BackendTypeLayoutMember member = layout.Members[i];
            members[i] = new TypeLayoutMemberResponse(member.Name, member.TypeDisplay, member.Offset, member.Size, member.Alignment);
        }

        var padding = new TypeLayoutPaddingResponse[layout.Padding.Count];
        for (int i = 0; i < padding.Length; i++)
        {
            BackendTypeLayoutPadding region = layout.Padding[i];
            padding[i] = new TypeLayoutPaddingResponse(region.Offset, region.Size, MapPaddingKind(region.Kind));
        }

        return new TypeLayoutResponse(
            layout.TypeDisplay,
            layout.TargetDisplay,
            layout.Size,
            layout.Alignment,
            layout.PayloadSize,
            layout.PaddingSize,
            layout.Stride,
            layout.ElementCount,
            layout.ElementSize,
            layout.ElementAlignment,
            members,
            padding);
    }

    /// <summary>
    /// Whether a padding run sits between members or after the last one, carried through verbatim so
    /// the client labels the run with the compiler's own classification.
    /// </summary>
    private static string MapPaddingKind(BackendTypeLayoutPaddingKind kind) => kind switch
    {
        BackendTypeLayoutPaddingKind.Tail => "tail",
        _ => "internal",
    };
}
