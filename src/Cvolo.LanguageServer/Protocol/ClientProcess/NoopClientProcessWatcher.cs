namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Watcher for unsupported platforms: process observations are declared as
/// best-effort unavailable and are never treated as client-death signals.
/// </summary>
internal sealed class NoopClientProcessWatcher : IClientProcessWatcher
{
    public bool IsProcessAlive(int processId) => true;

    public bool TryWatch(int processId, CancellationToken cancellationToken, Action onClientTerminated) => false;

    public void Dispose()
    {
    }
}