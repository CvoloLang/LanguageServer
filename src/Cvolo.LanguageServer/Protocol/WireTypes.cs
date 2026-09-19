using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Wire type for the <c>initialize</c> response. The protocol library's
/// <c>InitializeResult</c> has no <c>serverInfo</c> property, so the server
/// defines its own result shape per LSP 3.17.
/// </summary>
internal sealed record InitializeResponse(ServerCapabilities Capabilities, ServerInfo ServerInfo);

/// <summary>
/// LSP 3.17 server capabilities advertised by this server. Defined locally
/// because the protocol library marks <c>CompletionOptions.ResolveProvider</c>
/// with <c>EmitDefaultValue = false</c>, which silently drops an explicit
/// <c>resolveProvider: false</c> from the wire; LSP-3 requires it advertised
/// (§6.1).
/// </summary>
internal sealed record ServerCapabilities
{
    public TextDocumentSyncOptions? TextDocumentSync { get; init; }

    public CompletionOptions? CompletionProvider { get; init; }
}

/// <summary>
/// LSP 3.17 <c>completionProvider</c> options. A local shape so
/// <c>resolveProvider</c> is always serialized, including when it is false.
/// </summary>
internal sealed record CompletionOptions
{
    public bool ResolveProvider { get; init; }

    public string[]? TriggerCharacters { get; init; }
}

/// <summary>
/// LSP 3.17 <c>ServerInfo</c>.
/// </summary>
internal sealed record ServerInfo(string Name, string Version);

/// <summary>
/// Params for the <c>$/setTrace</c> notification.
/// </summary>
internal sealed record SetTraceParams(string? Value);

/// <summary>
/// Server-side shape of the <c>initialize</c> request params. The protocol
/// library's <c>InitializeParams</c> predates <c>workspaceFolders</c>, so the
/// server deserializes the payload into its own superset view.
/// </summary>
internal sealed class InitializeRequestParams
{
    public int? ProcessId { get; init; }
    public string? RootPath { get; init; }
    public Uri? RootUri { get; init; }
    public WorkspaceFolderItem[]? WorkspaceFolders { get; init; }
    public ClientCapabilitiesPayload? Capabilities { get; init; }
}

/// <summary>
/// Server-side shape of the subset of <c>ClientCapabilities</c> the server
/// reads. The protocol library's model does not surface
/// <c>textDocument.publishDiagnostics.relatedInformation</c>, so the server
/// deserializes its own minimal view.
/// </summary>
internal sealed class ClientCapabilitiesPayload
{
    public TextDocumentClientCapabilitiesPayload? TextDocument { get; init; }
}

internal sealed class TextDocumentClientCapabilitiesPayload
{
    public PublishDiagnosticsClientCapabilitiesPayload? PublishDiagnostics { get; init; }
}

internal sealed class PublishDiagnosticsClientCapabilitiesPayload
{
    public bool? RelatedInformation { get; init; }
}

/// <summary>
/// LSP 3.17 <c>WorkspaceFolder</c> wire shape (uri carried as a string).
/// </summary>
internal sealed record WorkspaceFolderItem(string? Uri, string? Name);