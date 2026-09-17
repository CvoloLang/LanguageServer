using Cvolo.LanguageServer.Tests.TestSupport;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class UnknownMethodTests
{
    [Fact]
    public async Task UnknownRequest_ReturnsMethodNotFound_AndServerStaysUsable()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync().WithTimeout("initialize");

        RemoteRpcException? error = await session.Client
            .TryUnknownRequestAsync()
            .WithTimeout("unknown request");

        Assert.NotNull(error);
        Assert.Equal(JsonRpcErrorCode.MethodNotFound, error!.ErrorCode);

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);
        Assert.False(session.Server.Termination.IsRequested);
    }

    [Fact]
    public async Task UnknownNotification_IsIgnored_NoResponse_NoCrash()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync().WithTimeout("initialize");
        session.Server.ResetServerFrames();

        await session.Client.NotifyUnknownAsync().WithTimeout("unknown notification");

        Assert.Empty(session.Server.GetServerFrames());

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);
        Assert.False(session.Server.Termination.AbnormalRequested);
    }
}