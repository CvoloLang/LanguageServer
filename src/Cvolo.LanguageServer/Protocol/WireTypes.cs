using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Wire type for the <c>initialize</c> response. The protocol library's
/// <c>InitializeResult</c> has no <c>serverInfo</c> property, so the server
/// defines its own result shape per LSP 3.17.
/// </summary>
internal sealed record InitializeResponse(ServerCapabilities Capabilities, ServerInfo ServerInfo);

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
}

/// <summary>
/// LSP 3.17 <c>WorkspaceFolder</c> wire shape (uri carried as a string).
/// </summary>
internal sealed record WorkspaceFolderItem(string? Uri, string? Name);