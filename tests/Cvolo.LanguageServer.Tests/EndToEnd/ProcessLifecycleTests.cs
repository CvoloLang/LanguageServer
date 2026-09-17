using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

public class ProcessLifecycleTests
{
    [Fact]
    public async Task ExitBeforeInitialize_ExitsNonZero_WithNoStdoutFrames()
    {
        using var server = ServerProcess.Start("--stdio");
        server.SendJson(JsonRpcFrames.Notification("exit", "null"));

        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(1, exitCode);
        Assert.Empty(server.SnapshotFrames());
    }

    [Fact]
    public async Task DeadClientProcessId_ReturnsInvalidParams_AndExitsNonZero()
    {
        var deadPid = UnusedProcessIdFinder.Find();
        using var server = ServerProcess.Start("--stdio");
        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{deadPid},\"rootPath\":null}}"));

        JObject response = await server.WaitForResponseAsync(1).WithTimeout("initialize response");
        Assert.Equal(-32602, response["error"]!["code"]!.Value<int>());

        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task AllStdoutBytes_AreValidJsonRpcFrames()
    {
        using var server = ServerProcess.Start("--stdio");
        await RunFullLifecycleAsync(server);
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);

        var stdout = await server.WaitForStdoutStableAsync().WithTimeout("stdout drain");
        (List<string> frames, var consumed) = FrameParser.Parse(stdout);
        Assert.Equal(stdout.Length, consumed);

        foreach (var frame in frames)
        {
            var json = JObject.Parse(frame);
            Assert.Equal("2.0", json["jsonrpc"]?.Value<string>());
        }
    }

    [Fact]
    public async Task EndToEnd_Lifecycle_ExitsCleanly_WithExpectedFrames()
    {
        using var server = ServerProcess.Start("--stdio");

        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"rootPath\":null}}"));
        JObject initialize = await server.WaitForResponseAsync(1).WithTimeout("initialize response");
        Assert.Equal("cvolo-language-server", initialize["result"]!["serverInfo"]!["name"]!.Value<string>());
        Assert.NotNull(initialize["result"]!["capabilities"]);

        server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));
        server.SendJson(JsonRpcFrames.Request(2, "shutdown", "null"));
        JObject shutdown = await server.WaitForResponseAsync(2).WithTimeout("shutdown response");
        Assert.Equal(JTokenType.Null, shutdown["result"]!.Type);

        server.SendJson(JsonRpcFrames.Notification("exit", "null"));
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task VersionFlag_PrintsExactlyThreeLines_ExitsZero()
    {
        using var server = ServerProcess.Start("--version");
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);

        var stdout = await server.WaitForStdoutStableAsync().WithTimeout("stdout drain");
        var lines = System.Text.Encoding.UTF8
            .GetString(stdout)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^cvolo-language-server \d+\.\d+\.\d+", lines[0]);
        Assert.Matches(@"^tooling \d+\.\d+\.\d+", lines[1]);
        Assert.Matches(@"^compiler-line \d+\.\d+", lines[2]);
    }

    private static async Task RunFullLifecycleAsync(ServerProcess server)
    {
        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"rootPath\":null}}"));
        await server.WaitForResponseAsync(1).WithTimeout("initialize response");

        server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));
        server.SendJson(JsonRpcFrames.Request(2, "shutdown", "null"));
        await server.WaitForResponseAsync(2).WithTimeout("shutdown response");

        server.SendJson(JsonRpcFrames.Notification("exit", "null"));
    }
}