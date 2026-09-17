using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class WatcherRobustnessTests
{
    [Fact]
    public async Task InitializedBeforeInitialize_DoesNotMutateState_AndSessionStaysUsable()
    {
        using var session = ProtocolSession.Start();

        await session.Client.NotifyInitializedAsync().WithTimeout("initialized before initialize");
        Assert.False(session.Server.Termination.IsRequested);
        Assert.False(session.Server.InitializedReceived, "a stray 'initialized' before 'initialize' must not set InitializedReceived");

        InitializeResponse response = await session.Client.InitializeAsync(processId: null).WithTimeout("initialize");
        Assert.Equal("cvolo-language-server", response.ServerInfo.Name);

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);

        await session.Client.NotifyExitAsync().WithTimeout("exit");
        var normal = await session.Server.Termination.Task.WithTimeout("termination");
        Assert.True(normal, "a stray 'initialized' before 'initialize' must not break the lifecycle");
    }

    [Fact]
    public async Task WatcherProbeFailure_AtInitialize_DegradesAndKeepsSessionAlive()
    {
        FakeClientProcessWatcher watcher = new() { ThrowOnProbe = new InvalidOperationException("probe failed") };
        using var session = ProtocolSession.Start(watcher: watcher);

        InitializeResponse response = await session.Client.InitializeAsync(processId: 4242).WithTimeout("initialize");
        Assert.Equal("cvolo-language-server", response.ServerInfo.Name);
        Assert.True(watcher.WatchCount == 0, "a failed probe must not start a watch");
        Assert.False(session.Server.Termination.IsRequested, "a probe failure is NOT client death");

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);

        await session.Client.NotifyExitAsync().WithTimeout("exit");
        var normal = await session.Server.Termination.Task.WithTimeout("termination");
        Assert.True(normal);
    }

    [Fact]
    public void WindowsWatcher_DeadPid_IsNotAlive_AndTryWatchReturnsFalseWithoutReport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsClientProcessWatcher watcher = new();
        var deadPid = UnusedProcessIdFinder.Find();
        Assert.False(watcher.IsProcessAlive(deadPid), "a dead pid must report as not alive");

        var invoked = 0;
        using CancellationTokenSource cts = new();
        var started = watcher.TryWatch(deadPid, cts.Token, () => Interlocked.Increment(ref invoked));

        Assert.False(started, "TryWatch on a dead pid must not start a watch");
        Assert.True(Volatile.Read(ref invoked) == 0, "dead-pid watch must never report client death");
    }
}