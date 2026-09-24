using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Compiler-authoritative signature help coverage (LSP-7 §45 semantics). The
/// server must render Tooling call-site facts (overloads, active signature and
/// active parameter, labels, label spans) and never re-derive them.
/// </summary>
public class SignatureHelpSemanticTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public SignatureHelpSemanticTests()
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

    private async Task OpenAsync(string marked)
    {
        (string text, _, _) = SplitCursor(marked);
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _session.Server.Store.TryGet(coreUri, out _),
            "didOpen applied",
            TimeSpan.FromSeconds(60));
    }

    private Task<SignatureHelp?> SignatureHelpAsync(string marked)
    {
        (_, int line, int character) = SplitCursor(marked);
        return _session.Client.SignatureHelpAsync(_workspace.DocumentUri("main.cvl"), line, character);
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

    private static string LabelText(SignatureInformation signature, ParameterInformation parameter)
    {
        Assert.True(parameter.Label.TryGetFirst(out string? label), "Expected a plain-string parameter label.");
        return label;
    }

    [Fact]
    public async Task TwoArgumentCall_ReportsSecondArgumentAsActive()
    {
        await InitializeAsync();
        const string source =
            "int Add(int left, int right) { return left + right; }\n" +
            "int main() { return Add(1, |); }\n";
        await OpenAsync(source);

        SignatureHelp? result = await SignatureHelpAsync(source);

        Assert.NotNull(result);
        SignatureInformation signature = Assert.Single(result!.Signatures);
        Assert.Equal("int Add(int left, int right)", signature.Label);
        Assert.Equal(0, result.ActiveSignature);
        Assert.Equal(1, result.ActiveParameter);
        Assert.Equal(2, signature.Parameters.Length);
        Assert.Equal("int left", LabelText(signature, signature.Parameters[0]));
        Assert.Equal("int right", LabelText(signature, signature.Parameters[1]));
    }

    [Fact]
    public async Task OverloadedCall_ListsAllOverloads_AndMarksResolvedSignatureActive()
    {
        await InitializeAsync();
        const string source =
            "int Add(int left, int right) { return left + right; }\n" +
            "int Add(int left, int right, int extra) { return left + right + extra; }\n" +
            "int main() { return Add(1, 2, |); }\n";
        await OpenAsync(source);

        SignatureHelp? result = await SignatureHelpAsync(source);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Signatures.Length);
        Assert.Equal("int Add(int left, int right)", result.Signatures[0].Label);
        Assert.Equal("int Add(int left, int right, int extra)", result.Signatures[1].Label);
        Assert.Equal(1, result.ActiveSignature);
        Assert.Equal(2, result.ActiveParameter);
    }

    [Fact]
    public async Task NestedCall_ReportsInnermostCallable()
    {
        await InitializeAsync();
        const string source =
            "int Max(int left, int right) { return 0; }\n" +
            "int Min(int left, int right) { return 0; }\n" +
            "int main() { return Max(1, Min(|)); }\n";
        await OpenAsync(source);

        SignatureHelp? result = await SignatureHelpAsync(source);

        Assert.NotNull(result);
        SignatureInformation signature = Assert.Single(result!.Signatures);
        Assert.Equal("int Min(int left, int right)", signature.Label);
        Assert.Equal(0, result.ActiveSignature);
        Assert.Equal(0, result.ActiveParameter);
    }

    [Fact]
    public async Task ZeroParameterCall_ReportsNullActiveParameter()
    {
        await InitializeAsync();
        const string source =
            "int Ping() { return 1; }\n" +
            "int main() { return Ping(|); }\n";
        await OpenAsync(source);

        SignatureHelp? result = await SignatureHelpAsync(source);

        Assert.NotNull(result);
        SignatureInformation signature = Assert.Single(result!.Signatures);
        Assert.Equal("int Ping()", signature.Label);
        Assert.Empty(signature.Parameters);
        Assert.Equal(0, result.ActiveSignature);
        Assert.Null(result.ActiveParameter);
    }

    [Fact]
    public async Task NameWithoutParens_ReturnsNull()
    {
        await InitializeAsync();
        const string source =
            "int Add(int left, int right) { return left + right; }\n" +
            "int main() { return Add|; }\n";
        await OpenAsync(source);

        SignatureHelp? result = await SignatureHelpAsync(source);

        Assert.Null(result);
    }

    [Fact]
    public async Task NonCallStatement_ReturnsNull()
    {
        await InitializeAsync();
        const string source = "int main() {\n    val int value = 1;\n    |\n}\n";
        await OpenAsync(source);

        SignatureHelp? result = await SignatureHelpAsync(source);

        Assert.Null(result);
    }

    [Fact]
    public async Task UnopenedDocument_ReturnsNull()
    {
        await InitializeAsync();

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(_workspace.DocumentUri("main.cvl"), 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task NonCvoloDocument_ReturnsNull()
    {
        await InitializeAsync();

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(_workspace.DocumentUri("notes.txt"), 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidPosition_ReturnsNull()
    {
        await InitializeAsync();
        const string source = "int Add(int left, int right) { return left + right; }\n";
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, source).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _session.Server.Store.TryGet(coreUri, out _),
            "didOpen applied",
            TimeSpan.FromSeconds(60));

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(_workspace.DocumentUri("main.cvl"), 0, 500);

        Assert.Null(result);
    }
}