using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

/// <summary>
/// Real-process hover/definition/documentSymbol coverage over stdio with a
/// known-valid fixture, asserting valid responses and a clean exit (§28.20).
/// </summary>
public class NavigationProcessTests
{
    [Fact]
    public async Task Navigation_OverStdio_ReturnsHoverDefinitionAndOutline_AndExitsCleanly()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "navigation");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-navigation-" + Guid.NewGuid().ToString("N"));
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
            int offset = text.IndexOf("Helper(p.x)", StringComparison.Ordinal);
            (int line, int character) = OffsetToPosition(text, offset);

            var uri = new Uri(mainPath).AbsoluteUri;
            string escaped = Escape(text);
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen", $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{escaped}\"}}}}"));

            server.SendJson(JsonRpcFrames.Request(2, "textDocument/hover", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject hover = await server.WaitForResponseAsync(2).WithTimeout("hover response");
            Assert.Null(hover["error"]);
            string hoverText = hover["result"]?["contents"]?.ToString() ?? string.Empty;
            Assert.Contains("Helper", hoverText);
            Assert.Equal(line, (int?)hover["result"]?["range"]?["start"]?["line"]);

            server.SendJson(JsonRpcFrames.Request(3, "textDocument/definition", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject definition = await server.WaitForResponseAsync(3).WithTimeout("definition response");
            Assert.Null(definition["error"]);
            var locations = definition["result"] as JArray;
            Assert.NotNull(locations);
            Assert.NotEmpty(locations!);

            server.SendJson(JsonRpcFrames.Request(4, "textDocument/documentSymbol", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}"));
            JObject symbols = await server.WaitForResponseAsync(4).WithTimeout("documentSymbol response");
            Assert.Null(symbols["error"]);
            var symbolArray = symbols["result"] as JArray;
            Assert.NotNull(symbolArray);
            Assert.NotEmpty(symbolArray!);

            server.SendJson(JsonRpcFrames.Request(5, "shutdown", "null"));
            await server.WaitForResponseAsync(5).WithTimeout("shutdown response");
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
