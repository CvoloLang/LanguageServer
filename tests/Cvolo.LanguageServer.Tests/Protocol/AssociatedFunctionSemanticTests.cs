using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// End-to-end protocol coverage for leading-dot associated functions. The declarations live in a
/// second document so every assertion also exercises cross-file resolution through the backend,
/// exactly as an editor would see it. The server owns no scope rules of its own, so these tests
/// only assert what Tooling reports over the protocol surface.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class AssociatedFunctionSemanticTests : IDisposable
{
    private const string LibSource =
        "struct MemBlock {\n" +
        "    nuint Size;\n" +
        "}\n" +
        "\n" +
        "extension MemBlock {\n" +
        "    nuint .AlignUp(nuint value, nuint alignment) { return value; }\n" +
        "    nuint Scaled(nuint factor) { return Size * factor; }\n" +
        "}\n";

    private const string MainSource =
        "int main() {\n" +
        "    var MemBlock block(Size: (nuint)8);\n" +
        "    val nuint a = MemBlock.AlignUp((nuint)3, (nuint)8);\n" +
        "    val nuint b = block.Scaled((nuint)2);\n" +
        "    return 0;\n" +
        "}\n";

    private static readonly string[] CanonicalTypes =
    [
        "namespace", "type", "struct", "enum", "interface", "typeParameter",
        "parameter", "variable", "property", "enumMember", "function", "method", "operator",
    ];

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public AssociatedFunctionSemanticTests()
    {
        _workspace = TestWorkspace.CreateProject(
            ["main.cvl", "lib.cvl"],
            relative => relative == "main.cvl" ? MainSource : LibSource);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Uri MainUri => _workspace.DocumentUri("main.cvl");

    private async Task InitializeAsync(string[]? hoverFormats = null)
    {
        await _session.Client
            .InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath, hoverContentFormat: hoverFormats, hierarchicalSymbols: false)
            .WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task InitializeSemanticTokensAsync()
    {
        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            ProcessId = null,
            RootUri = new Uri(_workspace.DirectoryPath),
            Capabilities = new ClientCapabilitiesPayload
            {
                TextDocument = new TextDocumentClientCapabilitiesPayload
                {
                    SemanticTokens = new SemanticTokensClientCapabilitiesPayload
                    {
                        Requests = new SemanticTokensRequestsPayload { Full = true },
                        TokenTypes = CanonicalTypes,
                        TokenModifiers = ["declaration", "readonly", "static"],
                        Formats = ["relative"],
                        AugmentsSyntaxTokens = true,
                    },
                },
            },
        }).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    /// <summary>
    /// Opens <c>main.cvl</c> with the supplied text and returns the cursor position. A single
    /// <c>|</c> marker is optional: when present it is stripped and becomes the cursor position,
    /// otherwise the cursor position is returned as <c>(0, 0)</c> for whole-document requests.
    /// </summary>
    private async Task<(int Line, int Character)> OpenMainAsync(string marked)
    {
        int marker = marked.IndexOf('|', StringComparison.Ordinal);
        string text = marked;
        var position = (Line: 0, Character: 0);
        if (marker >= 0)
        {
            text = marked.Remove(marker, 1);
            position = LineAndCharacter(text, marker);
        }

        await _session.Client.NotifyDidOpenAsync(MainUri, "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _session.Server.Store.TryGet(coreUri, out _),
            "didOpen applied",
            TimeSpan.FromSeconds(60));
        return position;
    }

    private async Task OpenLibAsync()
    {
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("lib.cvl"), "cvolo", 1, LibSource).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("lib.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _session.Server.Store.TryGet(coreUri, out _),
            "didOpen applied",
            TimeSpan.FromSeconds(60));
    }

    private async Task WaitForDiagnosticsAsync(string label)
    {
        await _session.Client.WaitUntilAsync(
            () => DiagnosticFrames.ForUri(_session.Server, MainUri).Count > 0,
            label,
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Completion_AfterTypeNameReceiver_OffersAssociatedFunction()
    {
        await InitializeAsync();
        var (line, character) = await OpenMainAsync(
            "int main() {\n" +
            "    val nuint a = MemBlock.Ali|\n" +
            "    return 0;\n" +
            "}\n");

        CompletionList? list = await _session.Client.CompletionAsync(MainUri, line, character).WithTimeout("completion");

        Assert.NotNull(list);
        Assert.Contains(list!.Items, item => item.Label == "AlignUp" && item.Kind == CompletionItemKind.Method);
    }

    [Fact]
    public async Task Completion_AfterValueReceiver_DoesNotOfferAssociatedFunction()
    {
        await InitializeAsync();
        var (line, character) = await OpenMainAsync(
            "int main() {\n" +
            "    var MemBlock block(Size: (nuint)8);\n" +
            "    val nuint a = block.Ali|\n" +
            "    return 0;\n" +
            "}\n");

        CompletionList? list = await _session.Client.CompletionAsync(MainUri, line, character).WithTimeout("completion");

        Assert.NotNull(list);
        Assert.DoesNotContain(list!.Items, item => item.Label == "AlignUp");
    }

    [Fact]
    public async Task Definition_FromTypeQualifiedCall_ReachesLeadingDotDeclaration()
    {
        await InitializeAsync();
        await OpenMainAsync(MainSource);

        var (line, character) = Position(MainSource, "AlignUp");
        Location[]? locations = await _session.Client.DefinitionAsync(MainUri, line, character).WithTimeout("definition");

        Assert.NotNull(locations);
        Location location = Assert.Single(locations!);
        Assert.Equal(_workspace.DocumentUri("lib.cvl").AbsoluteUri, location.Uri.AbsoluteUri);

        // The definition points at the identifier; the enclosing line is the leading-dot
        // declaration, not a plain instance method.
        var libText = File.ReadAllText(_workspace.PathOf("lib.cvl"));
        Assert.Equal("AlignUp", Slice(libText, location.Range));
        Assert.Contains(".AlignUp(nuint value, nuint alignment)", libText.Split('\n')[location.Range.Start.Line]);
    }

    [Fact]
    public async Task Hover_AtTypeQualifiedCall_ShowsSignatureWithoutReceiver()
    {
        await InitializeAsync();
        await OpenMainAsync(MainSource);

        var (line, character) = Position(MainSource, "AlignUp");
        Hover? hover = await _session.Client.HoverAsync(MainUri, line, character).WithTimeout("hover");

        Assert.NotNull(hover);
        var contents = (MarkupContent)hover!.Contents.Value!;
        Assert.Contains("AlignUp", contents.Value);
        Assert.Contains("nuint", contents.Value);
        Assert.DoesNotContain("this", contents.Value);
        Assert.Equal(character, hover.Range.Start.Character);
        Assert.Equal(character + "AlignUp".Length, hover.Range.End.Character);
    }

    [Fact]
    public async Task DocumentSymbol_ClassifiesAssociatedFunctionAsMethod()
    {
        await InitializeAsync();
        await OpenMainAsync(MainSource);
        await OpenLibAsync();

        JArray? symbols = await _session.Client.DocumentSymbolAsync(_workspace.DocumentUri("lib.cvl")).WithTimeout("documentSymbol");

        Assert.NotNull(symbols);
        var flat = Flatten(symbols!).ToList();

        // The associated function and the instance extension method are distinct declarations and
        // both surface as methods; neither is classified as a plain function.
        var alignUp = Assert.Single(flat.Where(s => s.Name == "AlignUp"));
        Assert.Equal((int)SymbolKind.Method, alignUp.Kind);
        var scaled = Assert.Single(flat.Where(s => s.Name == "Scaled"));
        Assert.Equal((int)SymbolKind.Method, scaled.Kind);
    }

    [Fact]
    public async Task SemanticTokens_ClassifyAssociatedFunctionIdentifierAsMethod()
    {
        await InitializeSemanticTokensAsync();
        await OpenMainAsync(MainSource);

        JObject? result = await _session.Client.SemanticTokensAsync(MainUri).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        var tokens = Decode(result!["data"]!.Values<int>().ToArray());
        var (callLine, callCharacter) = Position(MainSource, "AlignUp");

        // The identifier is classified as a method/function, never as a plain variable, and the
        // leading dot itself is left untokenized.
        Assert.Contains(tokens, t => t.Line == callLine && t.Character == callCharacter
            && t.Length == "AlignUp".Length
            && (t.Type == Array.IndexOf(CanonicalTypes, "method") || t.Type == Array.IndexOf(CanonicalTypes, "function")));
    }

    [Fact]
    public async Task Diagnostics_AssociatedFunctionCalledThroughValue_ReportsCvl1047()
    {
        await InitializeAsync();
        await OpenMainAsync(
            "int main() {\n" +
            "    var MemBlock block(Size: (nuint)8);\n" +
            "    val nuint a = block.AlignUp((nuint)3, (nuint)8);\n" +
            "    return 0;\n" +
            "}\n");
        await WaitForDiagnosticsAsync("diagnostics published");

        JArray diagnostics = DiagnosticFrames.ForUri(_session.Server, MainUri).Last().Diagnostics;
        Assert.Contains(diagnostics, d => d["code"]!.Value<string>() == "CVL1047");
    }

    [Fact]
    public async Task Diagnostics_InstanceMethodCalledThroughType_ReportsCvl1048()
    {
        await InitializeAsync();
        await OpenMainAsync(
            "int main() {\n" +
            "    val nuint a = MemBlock.Scaled((nuint)2);\n" +
            "    return 0;\n" +
            "}\n");
        await WaitForDiagnosticsAsync("diagnostics published");

        JArray diagnostics = DiagnosticFrames.ForUri(_session.Server, MainUri).Last().Diagnostics;
        Assert.Contains(diagnostics, d => d["code"]!.Value<string>() == "CVL1048");
    }

    private static IEnumerable<(string Name, int Kind)> Flatten(JArray symbols)
    {
        foreach (var symbol in symbols.OfType<JObject>())
        {
            yield return (symbol["name"]?.Value<string>() ?? string.Empty, symbol["kind"]?.Value<int>() ?? -1);
            if (symbol["children"] is JArray children)
            {
                foreach (var child in Flatten(children))
                {
                    yield return child;
                }
            }
        }
    }

    private static string Slice(string text, Microsoft.VisualStudio.LanguageServer.Protocol.Range range)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (range.Start.Line == range.End.Line)
        {
            return lines[range.Start.Line].Substring(range.Start.Character, range.End.Character - range.Start.Character);
        }

        var builder = new System.Text.StringBuilder(lines[range.Start.Line].Substring(range.Start.Character));
        for (int line = range.Start.Line + 1; line < range.End.Line; line++)
        {
            builder.Append('\n').Append(lines[line]);
        }

        return builder.Append('\n').Append(lines[range.End.Line].Substring(0, range.End.Character)).ToString();
    }

    private static (int Line, int Character) Position(string source, string needle, int delta = 0)
    {
        int index = source.IndexOf(needle, StringComparison.Ordinal) + delta;
        Assert.True(index >= 0, $"'{needle}' was not found in the fixture.");
        return LineAndCharacter(source, index);
    }

    private static (int Line, int Character) LineAndCharacter(string text, int index)
    {
        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
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

    private static List<(int Line, int Character, int Length, int Type, int Modifiers)> Decode(int[] data)
    {
        var tokens = new List<(int, int, int, int, int)>();
        var line = 0;
        var character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            tokens.Add((line, character, data[i + 2], data[i + 3], data[i + 4]));
        }

        return tokens;
    }
}
