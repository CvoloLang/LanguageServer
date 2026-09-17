namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Best-effort watcher over a client process. Watcher failures are logged and
/// must never corrupt the JSON-RPC transport. A watcher is only started when
/// <c>InitializeParams.ProcessId</c> is non-null.
/// </summary>
internal interface IClientProcessWatcher : IDisposable
{
    bool IsProcessAlive(int processId);

    /// <summary>
    /// Begins observing <paramref name="processId"/>. When the client dies,
    /// <paramref name="onClientTerminated"/> is invoked exactly once - unless
    /// <paramref name="cancellationToken"/> was cancelled first (the shutdown
    /// path disarms the watcher so a client-death race cannot force an
    /// abnormal exit). Returns false if the watch could not be established.
    /// </summary>
    bool TryWatch(int processId, CancellationToken cancellationToken, Action onClientTerminated);
}