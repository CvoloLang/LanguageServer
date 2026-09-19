using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class CompletionHandlerTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public CompletionHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"]);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private async Task InitializeAsync()
    {
        await _session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(string markedSource)
    {
        (string text, _, _) = SplitCursor(markedSource);
        await OpenTextAsync(text);
    }

    private async Task OpenTextAsync(string text)
    {
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(() => _session.Server.Store.TryGet(coreUri, out _), "didOpen applied");
    }

    private Task<CompletionList?> CompleteAsync(string markedSource)
    {
        (_, int line, int character) = SplitCursor(markedSource);
        return _session.Client.CompletionAsync(_workspace.DocumentUri("main.cvl"), line, character);
    }

    private static bool Has(CompletionList? list, string label, CompletionItemKind kind)
    {
        return list is not null && list.Items.Any(item => item.Label == label && item.Kind == kind);
    }

    private static (string Text, int Line, int Character) SplitCursor(string marked)
    {
        int index = marked.IndexOf('|');
        Assert.True(index >= 0, "Test fixture must contain a '|' cursor marker.");
        string text = marked.Remove(index, 1);

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

        return (text, line, character);
    }

    [Fact]
    public async Task ManualCompletion_ReturnsKeywordAtIncompletePosition()
    {
        await InitializeAsync();
        const string source = "int main() {\n    ret|\n}\n";
        await OpenAsync(source);

        CompletionList? result = await CompleteAsync(source);

        Assert.True(Has(result, "return", CompletionItemKind.Keyword));
    }

    [Fact]
    public async Task VisibleLocal_IsReturned()
    {
        await InitializeAsync();
        const string source = "int main() {\n    val int value = 1;\n    return value   |;\n}\n";
        await OpenAsync(source);

        CompletionList? result = await CompleteAsync(source);

        Assert.True(Has(result, "value", CompletionItemKind.Variable));
    }

    [Fact]
    public async Task MemberCompletion_ReturnsStructFields_AndReplacesPartialToken()
    {
        await InitializeAsync();
        const string source = "struct Point { int color; int count; }\nint main() {\n    val Point p;\n    return p.c|;\n}\n";
        await OpenAsync(source);

        CompletionList? result = await CompleteAsync(source);

        Assert.NotNull(result);
        Assert.True(Has(result, "color", CompletionItemKind.Field));
        Assert.True(Has(result, "count", CompletionItemKind.Field));
        Assert.DoesNotContain(result!.Items, item => item.Kind == CompletionItemKind.Keyword);

        (_, int line, int character) = SplitCursor(source);
        TextEdit edit = result.Items.First(item => item.Label == "color").TextEdit!;
        Assert.Equal(line, edit.Range.Start.Line);
        Assert.Equal(character - 1, edit.Range.Start.Character);
        Assert.Equal(character, edit.Range.End.Character);
        Assert.Equal("color", edit.NewText);
    }

    [Fact]
    public async Task BareDot_UsesZeroLengthReplacement()
    {
        await InitializeAsync();
        const string source = "struct Point { int x; int y; }\nint main() { val Point p; return p.|; }\n";
        await OpenAsync(source);

        CompletionList? result = await CompleteAsync(source);

        Assert.NotNull(result);
        Assert.True(Has(result, "x", CompletionItemKind.Field));
        TextEdit edit = result!.Items.First(item => item.Label == "x").TextEdit!;
        Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line);
        Assert.Equal(edit.Range.Start.Character, edit.Range.End.Character);
    }

    [Fact]
    public async Task ExtensionMethod_IsOfferedOnStructReceiver()
    {
        await InitializeAsync();
        const string source =
            "struct Point { int x; int y; }\n" +
            "extension Point {\n    int Area() { return 0; }\n}\n" +
            "int main() {\n    val Point p;\n    return p.A|;\n}\n";
        await OpenAsync(source);

        CompletionList? result = await CompleteAsync(source);

        Assert.True(Has(result, "Area", CompletionItemKind.Method));
    }

    [Fact]
    public async Task Utf16Coordinates_AfterSurrogatePair()
    {
        await InitializeAsync();
        const string source = "int main() {\n    val string s = \"\U0001F916\";\n    ret|\n}\n";
        await OpenAsync(source);

        CompletionList? result = await CompleteAsync(source);

        Assert.True(Has(result, "return", CompletionItemKind.Keyword));
    }

    [Fact]
    public async Task UnopenedDocument_ReturnsNull()
    {
        await InitializeAsync();

        CompletionList? result = await _session.Client.CompletionAsync(_workspace.DocumentUri("main.cvl"), 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task NonCvoloDocument_ReturnsNull()
    {
        await InitializeAsync();

        CompletionList? result = await _session.Client.CompletionAsync(_workspace.DocumentUri("notes.txt"), 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidPosition_ReturnsNull()
    {
        await InitializeAsync();
        await OpenTextAsync("int main() { return 0; }\n");

        CompletionList? result = await _session.Client.CompletionAsync(_workspace.DocumentUri("main.cvl"), 0, 500);

        Assert.Null(result);
    }

    [Fact]
    public async Task CompletionItemResolve_IsNotRegistered_AndSessionSurvives()
    {
        await InitializeAsync();

        var error = await _session.Client
            .ExpectErrorAsync("completionItem/resolve", new CompletionItem { Label = "x" })
            .WithTimeout("completionItem/resolve");

        Assert.Equal(-32601, (int?)error.ErrorCode);

        // The unsupported resolve request must not damage the session.
        const string source = "int main() {\n    ret|\n}\n";
        await OpenAsync(source);
        CompletionList? result = await CompleteAsync(source);
        Assert.True(Has(result, "return", CompletionItemKind.Keyword));
    }
}
