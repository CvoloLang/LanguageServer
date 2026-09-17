using Cvolo.LanguageServer.Protocol;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Deterministic client watcher for tests. It mirrors the production watcher's
/// contract: the termination callback is only invoked while the watch token is
/// still live, so a disarmed (cancelled) watch never reports client death.
/// </summary>
internal sealed class FakeClientProcessWatcher : IClientProcessWatcher
{
    private readonly Lock _gate = new();
    private Action? _onTerminated;

    public int WatchCount { get; private set; }
    public int? WatchedProcessId { get; private set; }
    public bool IsWatching { get; private set; }
    public CancellationToken WatchToken { get; private set; }

    /// <summary>
    /// When set, IsProcessAlive throws this exception (simulates a watcher/probe infrastructure failure).
    /// </summary>
    public Exception? ThrowOnProbe { get; set; }

    public bool IsProcessAlive(int processId)
    {
        if (ThrowOnProbe is not null)
        {
            throw ThrowOnProbe;
        }

        return true;
    }

    public bool TryWatch(int processId, CancellationToken cancellationToken, Action onClientTerminated)
    {
        lock (_gate)
        {
            WatchCount++;
            WatchedProcessId = processId;
            WatchToken = cancellationToken;
            IsWatching = true;
            _onTerminated = onClientTerminated;
        }

        return true;
    }

    /// <summary>
    /// Raises client death the way the real watcher would.
    /// </summary>
    public void SimulateClientDeath()
    {
        Action? callback;
        bool cancelled;
        lock (_gate)
        {
            callback = _onTerminated;
            cancelled = WatchToken.IsCancellationRequested;
        }

        if (callback is not null && !cancelled)
        {
            callback();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            IsWatching = false;
        }
    }
}