using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class SetTraceTests
{
    [Theory]
    [InlineData("off")]
    [InlineData("messages")]
    [InlineData("verbose")]
    public async Task SetTrace_AcceptsSupportedLevel_NoResponse_NoCrash(string value)
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync().WithTimeout("initialize");
        session.Server.ResetServerFrames();

        await session.Client.NotifySetTraceAsync(value).WithTimeout("$/setTrace");

        Assert.Empty(session.Server.GetServerFrames());

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);
        Assert.False(session.Server.Termination.IsRequested);
    }

    [Fact]
    public async Task SetTrace_InvalidValue_DoesNotFailSession()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync().WithTimeout("initialize");

        await session.Client.NotifySetTraceAsync("no-such-level").WithTimeout("$/setTrace");

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);
        Assert.False(session.Server.Termination.IsRequested);
    }
}