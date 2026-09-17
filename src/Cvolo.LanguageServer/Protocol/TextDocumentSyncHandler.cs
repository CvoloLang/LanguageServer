using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/didOpen, didChange and didClose handler. Converts wire payloads
/// into core documents, feeds the <see cref="DocumentStore"/>, and never emits
/// responses. All failures are non-fatal: the server stays alive and simply
/// does not publish state.
/// </summary>
internal sealed class TextDocumentSyncHandler(ILspLogger logger, Func<IReadOnlyList<string>> workspaceFolders, Func<string?> workspaceRoot)
{
    private const string CvoloLanguageId = "cvolo";

    private DocumentStore? _store;

    /// <summary>
    /// Lazily-built store; the workspace folders and fallback root from
    /// initialize are fixed for the session.
    /// </summary>
    internal DocumentStore Store => _store ??= CreateStore();

    [JsonRpcMethod(Methods.TextDocumentDidOpenName, UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams? parameters)
    {
        if (parameters?.TextDocument is not { } item)
        {
            logger.Warning("didOpen without a textDocument; ignored.");
            return;
        }

        if (!TryCreateDocumentUri(item.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"didOpen for a non-file document ('{item.Uri}') ignored.");
            return;
        }

        if (!IsCvoloDocument(documentUri))
        {
            logger.Debug($"didOpen for non-cvolo document '{documentUri}' ignored.");
            return;
        }

        if (!string.Equals(item.LanguageId, CvoloLanguageId, StringComparison.OrdinalIgnoreCase))
        {
            logger.Debug($"didOpen for '{documentUri}' reports languageId '{item.LanguageId}' (expected '{CvoloLanguageId}').");
        }

        Store.Open(documentUri, item.LanguageId, item.Version, item.Text);
    }

    [JsonRpcMethod(Methods.TextDocumentDidChangeName, UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams? parameters)
    {
        if (parameters?.TextDocument is not { } textDocument || parameters.ContentChanges is null)
        {
            logger.Warning("didChange without a textDocument or contentChanges; ignored.");
            return;
        }

        if (!TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"didChange for a non-file document ('{textDocument.Uri}') ignored.");
            return;
        }

        if (!IsCvoloDocument(documentUri))
        {
            return;
        }

        var changes = new List<DocumentChange>(parameters.ContentChanges.Length);
        foreach (var change in parameters.ContentChanges)
        {
            TextRange? range = change.Range is null ? null : ToCoreRange(change.Range);
            changes.Add(new DocumentChange(range, change.Text ?? string.Empty));
        }

        Store.ApplyChanges(documentUri, textDocument.Version, changes);
    }

    [JsonRpcMethod(Methods.TextDocumentDidCloseName, UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams? parameters)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Warning("didClose without a textDocument; ignored.");
            return;
        }

        if (!TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"didClose for a non-file document ('{textDocument.Uri}') ignored.");
            return;
        }

        if (!IsCvoloDocument(documentUri))
        {
            return;
        }

        Store.Close(documentUri);
    }

    private DocumentStore CreateStore()
    {
        IReadOnlyList<string> folders = workspaceFolders();
        var root = workspaceRoot();

        if (folders.Count > 0)
        {
            logger.Info($"Document synchronization active; {folders.Count} workspace folder(s).");
        }
        else if (root is not null)
        {
            logger.Info($"Document synchronization active; workspace root '{root}'.");
        }
        else
        {
            logger.Info("Document synchronization active; no workspace root (project discovery is unbounded).");
        }

        CoreLoggerBridge coreLogger = new(logger);
        return new DocumentStore(new CvoloLanguageBackend(folders, root, coreLogger), coreLogger);
    }

    private static bool TryCreateDocumentUri(Uri? uri, out DocumentUri documentUri)
    {
        if (uri is { IsAbsoluteUri: true } && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return DocumentUri.TryCreate(uri.LocalPath, out documentUri);
        }

        documentUri = default;
        return false;
    }

    private static bool IsCvoloDocument(DocumentUri documentUri)
    {
        var extension = Path.GetExtension(documentUri.LocalPath);
        return string.Equals(extension, ".cvl", StringComparison.OrdinalIgnoreCase);
    }

    private static TextRange ToCoreRange(LspRange range)
    {
        return new TextRange(
            new TextPosition(range.Start.Line, range.Start.Character),
            new TextPosition(range.End.Line, range.End.Character));
    }
}