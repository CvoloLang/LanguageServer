using System.Runtime.InteropServices;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Client process watcher for POSIX (Linux/macOS). On Linux, /proc metadata is
/// used with start-time tracking to reduce PID-reuse ambiguity; kill(pid, 0)
/// alone is never the sole detection mechanism. On systems without /proc
/// (macOS), kill(pid, 0) is used as best-effort with the downgrade noted.
/// </summary>
internal sealed class PosixClientProcessWatcher : IClientProcessWatcher
{
    private const int SigProbe = 0;
    private const int Esrch = 3;
    private const int Eperm = 1;

    public bool IsProcessAlive(int processId)
    {
        switch (ReadStartTime(processId, out _))
        {
            case ProcReadResult.Present:
                return true;
            case ProcReadResult.Missing:
                return ProbeExists(processId);
            default:
                // Probe infrastructure failure: NOT a confirmed death. The caller
                // degrades the session (no watcher, no abnormal termination).
                throw new InvalidOperationException($"Cannot probe process {processId}: /proc metadata could not be read.");
        }
    }

    public bool TryWatch(int processId, CancellationToken cancellationToken, Action onClientTerminated)
    {
        bool hasProc = ReadStartTime(processId, out long startTime) == ProcReadResult.Present;

        _ = Task.Run(async () => await WatchAsync(processId, hasProc, startTime, cancellationToken, onClientTerminated).ConfigureAwait(false));
        return true;
    }

    public void Dispose()
    {
    }

    private static async Task WatchAsync(
        int processId,
        bool hasProc,
        long startTime,
        CancellationToken cancellationToken,
        Action onClientTerminated)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                bool alive;
                if (hasProc)
                {
                    switch (ReadStartTime(processId, out long current))
                    {
                        case ProcReadResult.Present:
                            alive = current == startTime;
                            break;
                        case ProcReadResult.Missing:
                            alive = ProbeExists(processId);
                            break;
                        default:
                            return; // probe infra failure -> degrade, no death report
                    }
                }
                else
                {
                    alive = ProbeExists(processId);
                }

                if (!alive)
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
    }

    private enum ProcReadResult
    {
        Present,
        Missing,
        Error,
    }

    /// <summary>
    /// Reads the start time (field 22) of <c>/proc/&lt;pid&gt;/stat</c>.
    /// <see cref="ProcReadResult.Missing"/> means the process is gone (or the
    /// entry is unreadably gone); <see cref="ProcReadResult.Error"/> means the
    /// probe infrastructure failed, which must not be treated as client death.
    /// </summary>
    private static ProcReadResult ReadStartTime(int processId, out long startTime)
    {
        startTime = 0;
        try
        {
            string path = $"/proc/{processId}/stat";
            if (!File.Exists(path))
            {
                return ProcReadResult.Missing;
            }

            string stat = File.ReadAllText(path);
            int closingParen = stat.LastIndexOf(')');
            if (closingParen < 0)
            {
                return ProcReadResult.Missing;
            }

            string[] fields = stat.Substring(closingParen + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // After the ')', field 3 is state; starttime is field 22, i.e. index 19.
            if (fields.Length <= 19 || !long.TryParse(fields[19], out startTime))
            {
                return ProcReadResult.Missing;
            }

            return ProcReadResult.Present;
        }
        catch (IOException)
        {
            return ProcReadResult.Error;
        }
        catch (UnauthorizedAccessException)
        {
            return ProcReadResult.Error;
        }
    }

    /// <summary>kill(pid, 0): 0 => exists, EPERM => exists but not accessible.</summary>
    private static bool ProbeExists(int processId)
    {
        if (kill(processId, SigProbe) == 0)
        {
            return true;
        }

        return Marshal.GetLastWin32Error() == Eperm;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}