using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

/// <summary>
/// LSP-7 §49 process E2E over the real server process: initialize with snippet + signature
/// help capabilities, open a known-valid rich-completion fixture, complete a callable,
/// resolve one candidate, ask signature help for the enclosing and innermost calls, then
/// shut down cleanly. Asserts overloads are not collapsed and resolve never rewrites the
/// insertion template.
/// </summary>
public class RichFeaturesProcessTests
{
    [Fact]
    public async Task RichFeatures_OverStdio_CompleteResolveAndSignatureHelp_ThenExitCleanly()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "rich-completion");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-rich-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;

            string capabilities =
                "{\"textDocument\":{" +
                "\"completion\":{\"completionItem\":{\"snippetSupport\":true}}," +
                "\"signatureHelp\":{}}}";
            server.SendJson(JsonRpcFrames.Request(1, "initialize",
                $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}],\"capabilities\":{capabilities}}}"));
            JObject initialize = await server.WaitForResponseAsync(1).WithTimeout("initialize response");

            Assert.Equal(true, initialize["result"]?["capabilities"]?["completionProvider"]?["resolveProvider"]?.Value<bool>());
            Assert.NotNull(initialize["result"]?["capabilities"]?["signatureHelpProvider"]);
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainPath = Path.Combine(workspace, "main.cvl");
            string text = File.ReadAllText(mainPath);
            string uri = new Uri(mainPath).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(text)}\"}}}}"));

            // Completion: cursor right after the completed callable name `Add` in an
            // expression context (the exact form the semantic suites complete against).
            string anchor = "sum = Add(1, Min(1, 2));";
            string addTyped = "return Add";
            (int line, int character) = OffsetToPosition(text, text.IndexOf(addTyped, StringComparison.Ordinal) + addTyped.Length);
            server.SendJson(JsonRpcFrames.Request(2, "textDocument/completion",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject completion = await server.WaitForResponseAsync(2).WithTimeout("completion response");

            var items = completion["result"]?["items"] as JArray;
            Assert.NotNull(items);
            var adds = items!.Where(item => (string?)item["label"] == "Add").ToArray();
            Assert.Equal(2, adds.Length);
            Assert.Equal("int Add(int left, int right)", (string?)adds[0]!["detail"]);
            Assert.Equal("int Add(int left, int right, int total)", (string?)adds[1]!["detail"]);
            Assert.NotEqual((string?)adds[0]!["data"], (string?)adds[1]!["data"]);
            Assert.NotNull((string?)adds[0]!["data"]);
            Assert.NotNull((string?)adds[0]!["insertTextFormat"]);

            // Resolve: enrichment without rewriting the insertion template.
            server.SendJson(JsonRpcFrames.Request(3, "completionItem/resolve", adds[0].ToString(Formatting.None)));
            JObject resolved = await server.WaitForResponseAsync(3).WithTimeout("resolve response");
            Assert.Equal("Add", (string?)resolved["result"]!["label"]);
            Assert.Equal((string?)adds[0]!["detail"], (string?)resolved["result"]!["detail"]);
            Assert.Equal((string?)adds[0]!["insertTextFormat"], (string?)resolved["result"]!["insertTextFormat"]);
            Assert.NotNull(resolved["result"]?["documentation"]);
            Assert.Equal("plaintext", (string?)resolved["result"]!["documentation"]!["kind"]);
            Assert.Equal("Computes the sum of two numbers.", (string?)resolved["result"]!["documentation"]!["value"]);

            // Signature help: enclosing Add call, second argument active.
            string commaPosition = "sum = Add(1, ";
            (line, character) = OffsetToPosition(text, text.IndexOf(anchor, StringComparison.Ordinal) + commaPosition.Length);
            server.SendJson(JsonRpcFrames.Request(4, "textDocument/signatureHelp",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject signatureHelp = await server.WaitForResponseAsync(4).WithTimeout("signature help response");

            var signatures = signatureHelp["result"]?["signatures"] as JArray;
            Assert.NotNull(signatures);
            Assert.Equal(2, signatures!.Count);
            Assert.Equal("int Add(int left, int right)", (string?)signatures[0]!["label"]);
            Assert.Equal("int Add(int left, int right, int total)", (string?)signatures[1]!["label"]);
            Assert.Equal(0, (int)signatureHelp["result"]!["activeSignature"]);
            Assert.Equal(1, (int)signatureHelp["result"]!["activeParameter"]);

            // Signature help: innermost Min call wins.
            string minCall = "Min(1, 2)";
            (line, character) = OffsetToPosition(text, text.IndexOf(minCall, StringComparison.Ordinal) + "Min(".Length);
            server.SendJson(JsonRpcFrames.Request(5, "textDocument/signatureHelp",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject minHelp = await server.WaitForResponseAsync(5).WithTimeout("min signature help response");

            var minSignatures = minHelp["result"]?["signatures"] as JArray;
            Assert.NotNull(minSignatures);
            JToken minSignature = Assert.Single(minSignatures!);
            Assert.Equal("int Min(int left, int right)", (string?)minSignature["label"]);
            Assert.Equal(0, (int)minHelp["result"]!["activeSignature"]);
            Assert.Equal(0, (int)minHelp["result"]!["activeParameter"]);

            server.SendJson(JsonRpcFrames.Request(6, "shutdown", "null"));
            await server.WaitForResponseAsync(6).WithTimeout("shutdown response");
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