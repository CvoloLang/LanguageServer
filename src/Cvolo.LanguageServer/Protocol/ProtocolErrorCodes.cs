namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// JSON-RPC error codes used by the protocol foundation.
/// </summary>
internal static class ProtocolErrorCodes
{
    /// <summary>
    /// A request was received before the session was initialized (LSP-specific
    /// code, also used by VS Code's JSON-RPC implementation).
    /// </summary>
    public const int ServerNotInitialized = -32002;

    public const int RequestCancelled = -32800;
    public const int ContentModified = -32801;
    public const int RequestFailed = -32803;
}
