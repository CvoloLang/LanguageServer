using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/didOpen, didChange and didClose handler. Converts wire payloads
/// into core documents, feeds the session-scoped <see cref="DocumentStore"/>,
/// schedules diagnostics, and never emits responses. All failures are
/// non-fatal: the server stays alive and simply does not publish state.
/// </summary>
internal sealed class TextDocumentSyncHandler(
    ILspLogger logger,
    DiagnosticSink diagnostics,
    Func<DocumentStore> storeAccessor) : IDisposable
{
    private const string CvoloLanguageId = "cvolo";

    private DocumentStore? _store;
    private DiagnosticPublisher? _publisher;
    private DiagnosticScheduler? _scheduler;

    // Resolved on first use so the session-scoped store is created from the
    // workspace folders/root established by initialize, not at registration time.
    private DocumentStore Store => _store ??= storeAccessor();

    private DiagnosticPublisher Publisher => _publisher ??= new DiagnosticPublisher(logger, diagnostics);

    private DiagnosticScheduler Scheduler => _scheduler ??= new DiagnosticScheduler(Store, Publisher, logger);

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

        if (Store.Open(documentUri, item.LanguageId, item.Version, item.Text) is null)
        {
            logger.Warning($"[diag] didOpen for '{documentUri}' was rejected; diagnostics not scheduled.");
            return;
        }

        if (!Store.TryGetProject(documentUri, out BackendProject? project))
        {
            logger.Warning($"[diag] no backend project for '{documentUri}'; diagnostics not scheduled.");
            return;
        }

        logger.Info($"[diag] didOpen '{documentUri}' v{item.Version}; scheduling diagnostics.");
        Scheduler.Schedule(project);
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

        if (Store.ApplyChanges(documentUri, textDocument.Version, changes) is null)
        {
            logger.Debug($"[diag] didChange for '{documentUri}' v{textDocument.Version} was not applied; diagnostics not scheduled.");
            return;
        }

        if (!Store.TryGetProject(documentUri, out BackendProject? project))
        {
            logger.Warning($"[diag] no backend project for '{documentUri}'; diagnostics not scheduled.");
            return;
        }

        logger.Info($"[diag] didChange '{documentUri}' v{textDocument.Version}; scheduling diagnostics.");
        Scheduler.Schedule(project);
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

        BackendProject? project = Store.Close(documentUri);
        if (project is null)
        {
            logger.Debug($"[diag] didClose for '{documentUri}' ignored; document was not open.");
            return;
        }

        logger.Info($"[diag] didClose '{documentUri}'; cleared published diagnostics.");
        Publisher.PublishEmpty(documentUri);
        if (Store.HasOpenDocuments(project))
        {
            Scheduler.Schedule(project);
        }
        else
        {
            Scheduler.OnProjectDrained(project);
        }
    }

    public void Dispose()
    {
        _scheduler?.Dispose();
    }

    internal static bool TryCreateDocumentUri(Uri? uri, out DocumentUri documentUri)
    {
        if (uri is { IsAbsoluteUri: true } && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.LocalPath;

            // VS Code percent-encodes the Windows drive-letter colon
            // (file:///d%3A/...), which .NET surfaces as '/d:/...': a leading
            // slash that is not a valid fully-qualified Windows path. Strip it.
            if (OperatingSystem.IsWindows()
                && path.Length >= 3
                && path[0] == '/'
                && char.IsLetter(path[1])
                && path[2] == ':')
            {
                path = path.Substring(1);
            }

            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                documentUri = default;
                return false;
            }

            return DocumentUri.TryCreate(path, out documentUri);
        }

        documentUri = default;
        return false;
    }

    internal static bool IsCvoloDocument(DocumentUri documentUri)
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
