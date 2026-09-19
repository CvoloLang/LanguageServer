using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

/// <summary>
/// Real-process semantic-token coverage over stdio with a known-valid fixture (§30.28).
/// </summary>
public class SemanticTokensProcessTests
{
    [Fact]
    public async Task SemanticTokens_OverStdio_ReturnsRelativeEncodedTokens_AndExitsCleanly()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "semantic-tokens");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-semantic-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;
            const string capabilities =
                "{\"textDocument\":{\"semanticTokens\":{" +
                "\"requests\":{\"full\":true}," +
                "\"tokenTypes\":[\"namespace\",\"type\",\"struct\",\"enum\",\"interface\",\"typeParameter\",\"parameter\",\"variable\",\"property\",\"enumMember\",\"function\",\"method\",\"operator\"]," +
                "\"tokenModifiers\":[\"declaration\",\"readonly\",\"static\"]," +
                "\"formats\":[\"relative\"],\"augmentsSyntaxTokens\":true}}," +
                "\"workspace\":{\"semanticTokens\":{\"refreshSupport\":true}}}";
            server.SendJson(JsonRpcFrames.Request(100, "initialize", $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}],\"capabilities\":{capabilities}}}"));
            await server.WaitForResponseAsync(100).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainPath = Path.Combine(workspace, "main.cvl");
            string text = File.ReadAllText(mainPath);
            var uri = new Uri(mainPath).AbsoluteUri;
            string escaped = Escape(text);
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen", $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{escaped}\"}}}}"));
            await Task.Delay(1000);

            server.SendJson(JsonRpcFrames.Request(101, "textDocument/semanticTokens/full", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}"));
            JObject response = await server.WaitForResponseAsync(101).WithTimeout("semanticTokens response");

            Assert.Null(response["error"]);
            var data = response["result"]?["data"] as JArray;
            Assert.True(data is not null, "response=" + response.ToString(Newtonsoft.Json.Formatting.None));
            Assert.NotEmpty(data!);
            Assert.Equal(0, data!.Count % 5);

            server.SendJson(JsonRpcFrames.Request(102, "shutdown", "null"));
            await server.WaitForResponseAsync(102).WithTimeout("shutdown response");
            server.SendJson(JsonRpcFrames.Notification("exit", "null"));

            int exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Escape(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tooling.version")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (tooling.version missing).");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }
}
