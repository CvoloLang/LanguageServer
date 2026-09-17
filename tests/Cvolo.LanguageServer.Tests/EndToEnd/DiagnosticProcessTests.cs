using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

public class DiagnosticProcessTests
{
    private const string InvalidText = "int Main( { return 0; }";
    private const string ValidText = "int Main() { return 0; }";

    [Fact]
    public async Task Diagnostics_PublishAndClear_AcrossProcessBoundary()
    {
        using var workspace = TestWorkspace.CreateProject(["main.cvl"]);
        using var server = ServerProcess.Start("--stdio");

        var folderUri = new Uri(workspace.DirectoryPath).AbsoluteUri;
        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}]}}"));
        await server.WaitForResponseAsync(1).WithTimeout("initialize response");
        server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

        var uri = new Uri(Path.Combine(workspace.DirectoryPath, "main.cvl")).AbsoluteUri;

        server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen", $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{InvalidText}\"}}}}"));
        var opened = await WaitForPublishAsync(server, uri, list => list.Count >= 1 && list[^1].Count > 0).WithTimeout("didOpen diagnostics");
        Assert.Equal("cvolo", (string?)opened[^1].Diagnostics[0]["source"]);

        server.SendJson(JsonRpcFrames.Notification("textDocument/didChange", $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"version\":2}},\"contentChanges\":[{{\"text\":\"{ValidText}\"}}]}}"));
        await WaitForPublishAsync(server, uri, list => list.Count >= 2 && list[^1].Count == 0).WithTimeout("didChange cleared diagnostics");

        server.SendJson(JsonRpcFrames.Notification("textDocument/didClose", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}"));
        await WaitForPublishAsync(server, uri, list => list.Count >= 3 && list[^1].Count == 0).WithTimeout("didClose cleared diagnostics");

        server.SendJson(JsonRpcFrames.Request(2, "shutdown", "null"));
        await server.WaitForResponseAsync(2).WithTimeout("shutdown response");
        server.SendJson(JsonRpcFrames.Notification("exit", "null"));

        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);

        var stdout = await server.WaitForStdoutStableAsync().WithTimeout("stdout drain");
        (List<string> frames, var consumed) = FrameParser.Parse(stdout);
        Assert.Equal(stdout.Length, consumed);
        Assert.NotEmpty(frames);
    }

    private static async Task<IReadOnlyList<PublishedDiagnostics>> WaitForPublishAsync(ServerProcess server, string uri, Func<IReadOnlyList<PublishedDiagnostics>, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var target = new Uri(uri);
        while (DateTime.UtcNow < deadline)
        {
            var published = DiagnosticFrames.ForUri(server.SnapshotFrames(), target);
            if (predicate(published))
            {
                return published;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for publishDiagnostics for {uri}. stderr: {server.GetStderrText()}");
    }
}
