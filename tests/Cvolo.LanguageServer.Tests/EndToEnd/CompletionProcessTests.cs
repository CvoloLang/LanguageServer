using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

public class CompletionProcessTests
{
    [Fact]
    public async Task Completion_OverStdio_ReturnsReceiverMembers_AndExitsCleanly()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "completion");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-completion-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}]}}"));
            await server.WaitForResponseAsync(1).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainPath = Path.Combine(workspace, "main.cvl");
            string text = File.ReadAllText(mainPath);
            int offset = text.IndexOf("return p.", StringComparison.Ordinal) + "return p.".Length;
            (int line, int character) = OffsetToPosition(text, offset);

            var uri = new Uri(mainPath).AbsoluteUri;
            string escaped = text
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen", $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{escaped}\"}}}}"));

            server.SendJson(JsonRpcFrames.Request(2, "textDocument/completion", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject response = await server.WaitForResponseAsync(2).WithTimeout("completion response");

            var items = response["result"]?["items"] as JArray;
            Assert.NotNull(items);
            Assert.Contains(items!, item => (string?)item["label"] == "x");
            Assert.Contains(items!, item => (string?)item["label"] == "y");

            server.SendJson(JsonRpcFrames.Request(3, "shutdown", "null"));
            await server.WaitForResponseAsync(3).WithTimeout("shutdown response");
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

    private static (int Line, int Character) OffsetToPosition(string text, int offset)
    {
        var line = 0;
        var character = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                character = 0;
            }
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return (line, character);
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
