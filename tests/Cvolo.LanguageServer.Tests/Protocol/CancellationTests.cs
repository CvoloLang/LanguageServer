using Cvolo.LanguageServer.Tests.TestSupport;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class CancellationTests
{
    [Fact]
    public async Task CancelRequest_DoesNotCorruptLaterRequests_AndNeverCrashes()
    {
        using var session = ProtocolSession.Start(configureRpc: rpc =>
            rpc.AddLocalRpcMethod(
                "cvolo/delayed",
                new Func<CancellationToken, Task<object?>>(async token =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                    return null;
                })));
        await session.Client.InitializeAsync().WithTimeout("initialize");

        // Drive cancellation through the client's own request tracker so the
        // server's cancellation reply matches a tracked id (an untracked reply
        // would be treated by the client as a torn connection).
        using var cts = new CancellationTokenSource();
        Task<object?> delayed = session.Client.InvokeDelayedAsync(cts.Token);
        cts.Cancel();
        await SwallowAsync(delayed, "cancelled request");

        // Cancelling an id the client never issued is a plain notification:
        // the server ignores it and never replies, so it cannot disturb the stream.
        session.Client.SendRawJson(JsonRpcFrames.Cancel(999999));

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);
        Assert.False(session.Server.Termination.IsRequested, "cancellation plumbing must not corrupt the session");
    }

    [Fact]
    public async Task CancelUnknownOrCompletedRequest_IsHarmless()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync().WithTimeout("initialize");

        session.Client.SendRawJson(JsonRpcFrames.Cancel(1));
        session.Client.SendRawJson(JsonRpcFrames.Cancel(int.MaxValue));

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);
        Assert.False(session.Server.Termination.IsRequested);
    }

    private static async Task SwallowAsync(Task task, string operation)
    {
        try
        {
            await task.WithTimeout(operation);
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Both outcomes are legitimate: the request was cancelled.
        }
    }
}