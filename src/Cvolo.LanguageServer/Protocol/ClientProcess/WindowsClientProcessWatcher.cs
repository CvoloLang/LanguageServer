using System.Diagnostics;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Client process watcher for Windows. A process handle obtained from
/// <see cref="Process.GetProcessById(int)"/> is held for the whole watch so
/// PID reuse cannot confuse observability with the init process.
/// </summary>
internal sealed class WindowsClientProcessWatcher : IClientProcessWatcher
{
    private readonly List<Process> _watched = [];
    private readonly Lock _gate = new();

    public bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryWatch(int processId, CancellationToken cancellationToken, Action onClientTerminated)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        lock (_gate)
        {
            _watched.Add(process);
        }

        _ = Task.Run(async () => await WatchAsync(process, cancellationToken, onClientTerminated).ConfigureAwait(false));
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (Process process in _watched)
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }

            _watched.Clear();
        }
    }

    private async Task WatchAsync(Process process, CancellationToken cancellationToken, Action onClientTerminated)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                bool exited;
                try
                {
                    exited = process.HasExited;
                }
                catch (ObjectDisposedException)
                {
                    return; // watcher disarmed
                }
                catch (InvalidOperationException)
                {
                    return; // process never started or handle disposed
                }

                if (exited)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onClientTerminated();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Unexpected watcher/probe failure: degrade (stop watching) best-effort.
            // This is NOT a confirmed client death, so onClientTerminated must
            // never be invoked from an infrastructure error.
        }
        finally
        {
            lock (_gate)
            {
                _watched.Remove(process);
            }

            process.Dispose();
        }
    }
}