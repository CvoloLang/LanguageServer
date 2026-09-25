using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
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

    private async Task InitializeSnippetCapableAsync()
    {
        var capabilities = new ClientCapabilitiesPayload
        {
            TextDocument = new TextDocumentClientCapabilitiesPayload
            {
                Completion = new CompletionClientCapabilitiesPayload
                {
                    CompletionItem = new CompletionItemClientCapabilitiesPayload
                    {
                        SnippetSupport = true,
                    },
                },
            },
        };
        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            ProcessId = null,
            RootUri = new Uri(_workspace.DirectoryPath),
            Capabilities = capabilities,
        }).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(string name, string marked)
    {
        (string text, _, _) = SplitCursor(marked);
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri(name), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(
            () => _session.Server.Store.TryGet(coreUri, out _),
            "didOpen applied",
            TimeSpan.FromSeconds(60));
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
        await InitializeSnippetCapableAsync();
        const string source = "struct Name { int value; }\nextension Name {\n    ~|\n}\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items);
        Assert.Equal("~Name()", item.Label);
        Assert.Equal(InsertTextFormat.Snippet, item.InsertTextFormat);
        Assert.Equal("~Name() {\n    ${1:}\n}", item.TextEdit!.NewText);
        Assert.Equal(CompletionItemKind.Method, item.Kind);
    }

    [Fact]
    public async Task OverloadedCallable_OffersDistinctCandidates_WithDetails_AndResolveData()
    {
        // LSP-7 §21: overloads must stay distinct; the initial list carries a short
        // compiler-owned detail per overload and an opaque resolve token per candidate.
        await InitializeAsync();
        const string source =
            "int Add(int left, int right) { return left + right; }\n" +
            "int Add(int left, int right, int total) { return total; }\n" +
            "int main() { return Add|; }\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);

        Assert.NotNull(result);
        CompletionItem[] adds = result!.Items.Where(item => item.Label == "Add").ToArray();
        Assert.Equal(2, adds.Length);
        Assert.Equal("int Add(int left, int right)", adds[0].Detail);
        Assert.Equal("int Add(int left, int right, int total)", adds[1].Detail);
        Assert.NotEqual(adds[0].Detail, adds[1].Detail);
        Assert.All(adds, item => Assert.NotNull(item.Data));
        Assert.All(adds, item => Assert.IsType<string>(item.Data));
        Assert.NotEqual(adds[0].Data, adds[1].Data);
    }

    [Fact]
    public async Task CallableCandidate_ResolvesDetailAndDocumentation()
    {
        // LSP-7 §25/§30: completionItem/resolve fills compiler-owned detail and
        // documentation for a candidate minted against the current snapshot.
        await InitializeAsync();
        const string source =
            "/// Computes the sum of two numbers.\n" +
            "int Add(int left, int right) { return left + right; }\n" +
            "int main() { return Add(|); }\n";
        await OpenAsync("main.cvl", source);

        CompletionList? complete = await CompleteAsync("main.cvl", source);
        Assert.NotNull(complete);
        CompletionItem item = Assert.Single(complete!.Items, candidate => candidate.Label == "Add");
        Assert.NotNull(item.Data);

        var resolved = await _session.Client
            .ResolveCompletionItemAsync(item)
            .WithTimeout("completionItem/resolve");

        Assert.NotNull(resolved);
        Assert.Equal("int Add(int left, int right)", resolved!.Detail);
        Assert.True(resolved.Documentation.HasValue);
        Assert.True(resolved.Documentation!.Value.TryGetSecond(out MarkupContent? documentation));
        Assert.Equal(MarkupKind.PlainText, documentation.Kind);
        Assert.Equal("Computes the sum of two numbers.", documentation.Value);
        Assert.Equal(item.Label, resolved.Label);
    }

    [Fact]
    public async Task SnippetCapableClient_ReceivesSnippetTextEdit_ForCallable()
    {
        // LSP-7 §23: a snippet-capable client gets a compiler-owned insertion template
        // (numbered tab stops, $0 final cursor); a plain client gets the plain text.
        await InitializeSnippetCapableAsync();
        const string source =
            "int Add(int left, int right) { return left + right; }\n" +
            "int main() { return Add|; }\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);
        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items, candidate => candidate.Label == "Add");
        Assert.Equal(InsertTextFormat.Snippet, item.InsertTextFormat);
        Assert.Equal("Add(${1:left}, ${2:right})$0", item.TextEdit!.NewText);
    }

    [Fact]
    public async Task PlainClient_ReceivesPlainTextEdit_ForCallable()
    {
        // The same source without snippetSupport must never expose snippet syntax.
        await InitializeAsync();
        const string source =
            "int Add(int left, int right) { return left + right; }\n" +
            "int main() { return Add|; }\n";
        await OpenAsync("main.cvl", source);

        CompletionList? result = await CompleteAsync("main.cvl", source);
        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items, candidate => candidate.Label == "Add");
        Assert.Equal(InsertTextFormat.Plaintext, item.InsertTextFormat);
        Assert.Equal("Add()", item.TextEdit!.NewText);
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
