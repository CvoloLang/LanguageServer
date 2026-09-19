using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Compiler-authoritative semantic completion coverage. The language server must
/// not reimplement scope/member rules; these tests only assert what Tooling
/// reports through the protocol surface.
/// </summary>
public class CompletionSemanticTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public CompletionSemanticTests()
    {
        _workspace = TestWorkspace.CreateProject(
            ["main.cvl", "lib.cvl"],
            relative => relative == "lib.cvl"
                ? "global int SHARED_GLOBAL = 1;\n"
                : "int main() { return 0; }\n");
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

    private async Task OpenAsync(string name, string marked)
    {
        (string text, _, _) = SplitCursor(marked);
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri(name), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(() => _session.Server.Store.TryGet(coreUri, out _), "didOpen applied");
    }

    private Task<CompletionList?> CompleteAsync(string name, string marked)
    {
        (_, int line, int character) = SplitCursor(marked);
        return _session.Client.CompletionAsync(_workspace.DocumentUri(name), line, character);
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
    public async Task ProjectVisibleGlobal_FromAnotherFile_IsOffered()
    {
        await InitializeAsync();
        const string source = "int main() { return SHARED_| ; }\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.True(Has(result, "SHARED_GLOBAL", CompletionItemKind.Variable));
    }

    [Fact]
    public async Task OutOfScopeLocal_FromAnotherFunction_IsNotOffered()
    {
        await InitializeAsync();
        const string source =
            "int helper() { val int hidden = 1; return hidden; }\n" +
            "int main() {\n" +
            "    |\n" +
            "}\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, item => item.Label == "hidden");
        Assert.True(Has(result, "main", CompletionItemKind.Function));
    }

    [Fact]
    public async Task InScopeLocal_IsOffered_ButLaterDeclarationIsNot()
    {
        await InitializeAsync();
        const string source =
            "int main() {\n" +
            "    val int visible = 1;\n" +
            "    |\n" +
            "    val int later = 2;\n" +
            "}\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.True(Has(result, "visible", CompletionItemKind.Variable));
        Assert.DoesNotContain(result!.Items, item => item.Label == "later");
    }

    [Fact]
    public async Task MemberCompletion_DoesNotLeakUnrelatedGlobalsOrKeywords()
    {
        await InitializeAsync();
        const string source =
            "struct Point { int x; int y; }\n" +
            "int main() {\n" +
            "    val Point p;\n" +
            "    return p.|;\n" +
            "}\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.NotNull(result);
        Assert.True(Has(result, "x", CompletionItemKind.Field));
        Assert.True(Has(result, "y", CompletionItemKind.Field));
        Assert.DoesNotContain(result!.Items, item => item.Label == "SHARED_GLOBAL");
        Assert.DoesNotContain(result.Items, item => item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public async Task BareDot_BeforeFollowingStatement_OffersMembers_WithoutKeywords()
    {
        // Editor-realistic incomplete member access: `receiver.` on its own line
        // followed by another statement (no semicolon yet).
        await InitializeAsync();
        const string source =
            "struct Name { int value; }\n" +
            "int Subtract(int left, int right) {\n" +
            "    val Name n;\n" +
            "    n.|\n" +
            "    return left - right;\n" +
            "}\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.NotNull(result);
        Assert.True(Has(result, "value", CompletionItemKind.Field));
        Assert.DoesNotContain(result!.Items, item => item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public async Task TopLevelPartialKeyword_OffersDeclarationKeywords()
    {
        await InitializeAsync();
        const string source = "struct A { int x; }\nali|\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.True(Has(result, "alias", CompletionItemKind.Keyword));
    }

    [Fact]
    public async Task ExtensionDestructor_OffersSnippetCompletion()
    {
        await InitializeAsync();
        const string source = "struct Name { int value; }\nextension Name {\n    ~|\n}\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items);
        Assert.Equal("~Name()", item.Label);
        Assert.Equal(InsertTextFormat.Snippet, item.InsertTextFormat);
        Assert.Contains("~Name()", item.TextEdit!.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommentPosition_ReturnsEmptyList_NotNull()
    {
        await InitializeAsync();
        const string source = "int main() {\n    val int x = 1;\n    return x; // a|bc\n}\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.NotNull(result);
        Assert.Empty(result!.Items);
    }
}
