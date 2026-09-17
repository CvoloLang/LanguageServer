using System.Diagnostics;

namespace Cvolo.LanguageServer.Tests.TestSupport;

internal static class UnusedProcessIdFinder
{
    /// <summary>Returns a process id that certainly does not exist.</summary>
    public static int Find()
    {
        for (var pid = 1_000_000; pid < 1_001_000; pid++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
            catch (InvalidOperationException)
            {
                return pid;
            }
        }

        throw new InvalidOperationException("Could not find an unused process id to target.");
    }
}