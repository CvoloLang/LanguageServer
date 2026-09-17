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