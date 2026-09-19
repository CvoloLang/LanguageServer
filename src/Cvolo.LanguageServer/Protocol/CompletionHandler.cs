using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/completion handler. Validates the wire request, captures a
/// coherent semantic context, maps the UTF-16 line/character position to an
/// absolute offset over the captured text, queries the backend from that one
/// immutable snapshot, suppresses stale results, and maps the backend-neutral
/// result into an LSP completion list. It owns no Cvolo semantics (§24).
/// </summary>
internal sealed class CompletionHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    // Resolved on first use so the session-scoped store is created from the
    // workspace folders/root established by initialize, not at registration time.
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentCompletionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList?> Completion(CompletionParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[completion] request without a textDocument; ignored.");
            return null;
        }

        Position position = parameters.Position;
        logger.Debug($"[completion] request {textDocument.Uri} {position.Line}:{position.Character}");

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"[completion] non-file document '{textDocument.Uri}' ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[completion] non-cvolo document '{documentUri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[completion] '{documentUri}' is not open; no completion.");
            return null;
        }

        logger.Debug($"[completion] captured session={context.SessionId} version={context.Version}");

        // The request position is mapped against the exact captured text so the
        // replacement span is always valid over the text the offset refers to.
        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[completion] invalid request position {position.Line}:{position.Character} for '{documentUri}'.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        BackendCompletionResult result;
        try
        {
            // Run the (synchronous) semantic query off the JSON-RPC receive loop so
            // a slow query cannot stall other requests or cancellation (§19, §22).
            result = await Task.Run(() => Store.GetCompletions(context, offset), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[completion] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[completion] discarded stale result for '{documentUri}'.");
            return null;
        }

        logger.Debug($"[completion] backend returned {result.Items.Count} candidate(s) for '{documentUri}'.");

        return Map(result, index, offset, context.Document.Text.Length);
    }

    private CompletionList? Map(BackendCompletionResult result, LineIndex index, int offset, int textLength)
    {
        TextSpan span = result.ReplacementSpan;

        // Never clamp or fabricate a replacement range: a span that is outside the
        // captured text or does not contain the request offset is discarded (§17).
        if (span.Start < 0 || span.Length < 0 || span.End > textLength)
        {
            logger.Debug("[completion] discarding result with an out-of-range replacement span.");
            return null;
        }

        if (offset < span.Start || offset > span.End)
        {
            logger.Debug("[completion] discarding result whose replacement span does not contain the request offset.");
            return null;
        }

        if (!index.TryGetRange(span, out TextRange range) || range.Start.Line != range.End.Line)
        {
            logger.Debug("[completion] discarding result whose replacement span is not a single-line range.");
            return null;
        }

        var editRange = new LspRange
        {
            Start = new Position(range.Start.Line, range.Start.Character),
            End = new Position(range.End.Line, range.End.Character),
        };

        var items = new CompletionItem[result.Items.Count];
        for (var i = 0; i < result.Items.Count; i++)
        {
            BackendCompletionItem item = result.Items[i];
            items[i] = new CompletionItem
            {
                Label = item.Label,
                Kind = MapKind(item.Kind),
                InsertTextFormat = item.IsSnippet ? InsertTextFormat.Snippet : InsertTextFormat.Plaintext,
                TextEdit = new TextEdit
                {
                    Range = editRange,
                    NewText = item.InsertText,
                },
            };
        }

        return new CompletionList
        {
            IsIncomplete = false,
            Items = items,
        };
    }

    private static CompletionItemKind MapKind(BackendCompletionKind kind)
    {
        return kind switch
        {
            BackendCompletionKind.Keyword => CompletionItemKind.Keyword,
            BackendCompletionKind.Local => CompletionItemKind.Variable,
            BackendCompletionKind.Parameter => CompletionItemKind.Variable,
            BackendCompletionKind.Global => CompletionItemKind.Variable,
            BackendCompletionKind.Constant => CompletionItemKind.Constant,
            BackendCompletionKind.Function => CompletionItemKind.Function,
            BackendCompletionKind.Method => CompletionItemKind.Method,
            BackendCompletionKind.ExtensionMethod => CompletionItemKind.Method,
            BackendCompletionKind.Constructor => CompletionItemKind.Constructor,
            BackendCompletionKind.Field => CompletionItemKind.Field,
            BackendCompletionKind.StructField => CompletionItemKind.Field,
            BackendCompletionKind.Property => CompletionItemKind.Property,
            BackendCompletionKind.Struct => CompletionItemKind.Struct,
            BackendCompletionKind.Union => CompletionItemKind.Struct,
            BackendCompletionKind.Enum => CompletionItemKind.Enum,
            BackendCompletionKind.EnumMember => CompletionItemKind.EnumMember,
            BackendCompletionKind.EnumMetadata => CompletionItemKind.Property,
            BackendCompletionKind.Interface => CompletionItemKind.Interface,
            BackendCompletionKind.Protocol => CompletionItemKind.Interface,
            BackendCompletionKind.Module => CompletionItemKind.Module,
            BackendCompletionKind.Namespace => CompletionItemKind.Module,
            BackendCompletionKind.TypeAlias => CompletionItemKind.Class,
            BackendCompletionKind.Type => CompletionItemKind.Class,
            BackendCompletionKind.OtherType => CompletionItemKind.Class,
            BackendCompletionKind.UnionVariant => CompletionItemKind.Field,
            BackendCompletionKind.ArrayLength => CompletionItemKind.Property,
            _ => CompletionItemKind.Text,
        };
    }
}
