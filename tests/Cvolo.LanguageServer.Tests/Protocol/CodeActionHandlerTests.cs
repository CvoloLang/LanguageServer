using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using CoreTextSpan = Cvolo.LanguageServer.Core.Diagnostics.TextSpan;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class CodeActionHandlerTests : IDisposable
{
    private const string DocumentText = "int main()\n{\n    float f = 1.0;\n    return 0;\n}\n";
    private const string FixTitle = "Add 'f' suffix to make it a float literal";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public CodeActionHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(["a.cvl"]);
        _backend = new BlockingBackend();
        _store = new DocumentStore(_backend, new RecordingCoreLogger());
        _session = ProtocolSession.Start(storeFactory: () => _store);
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private static LspRange Cursor => new() { Start = new Position(0, 0), End = new Position(0, 0) };

    private DocumentUri CoreDocumentUri() => DocumentUri.Create(_workspace.PathOf("a.cvl"));

    private async Task<InitializeResponse> InitializeAsync(bool lazy, bool documentChanges = true)
    {
        var capabilities = new ClientCapabilitiesPayload
        {
            TextDocument = new TextDocumentClientCapabilitiesPayload
            {
                CodeAction = new CodeActionClientCapabilitiesPayload
                {
                    CodeActionLiteralSupport = new CodeActionLiteralSupportPayload
                    {
                        CodeActionKind = new CodeActionKindPayload { ValueSet = ["quickfix"] },
                    },
                    DataSupport = lazy,
                    ResolveSupport = lazy ? new CodeActionResolveSupportPayload { Properties = ["edit"] } : null,
                },
            },
            Workspace = new WorkspaceClientCapabilitiesPayload
            {
                WorkspaceEdit = new WorkspaceEditClientCapabilitiesPayload { DocumentChanges = documentChanges },
            },
        };

        InitializeResponse response = await _session.Client
            .InitializeWithAsync(new InitializeRequestParams
            {
                ProcessId = null,
                RootUri = new Uri(_workspace.DirectoryPath),
                Capabilities = capabilities,
            })
            .WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        return response;
    }

    private async Task OpenAsync()
    {
        await _session.Client
            .NotifyDidOpenAsync(_workspace.DocumentUri("a.cvl"), "cvolo", 1, DocumentText)
            .WithTimeout("didOpen");
        DocumentUri coreUri = CoreDocumentUri();
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private void ConfigureFloatFix()
    {
        DocumentUri coreUri = CoreDocumentUri();
        int literalStart = DocumentText.IndexOf("1.0", StringComparison.Ordinal);
        _backend.CannedCodeFixes = new BackendCodeFixResult(
        [
            new BackendCodeFixInfo(
                _backend.CreateCodeFixHandle(),
                FixTitle,
                [new BackendDiagnostic(
                    BackendDiagnosticSeverity.Error,
                    "CVL1900",
                    "Cannot implicitly convert double literal to float.",
                    new BackendDiagnosticLocation(coreUri, new CoreTextSpan(literalStart, 3), null),
                    [])]),
        ]);

        _backend.CannedCodeFixResolution = new BackendCodeFixSuccess(
            new Dictionary<DocumentUri, string> { [coreUri] = DocumentText },
            [new BackendCodeFixEdit(coreUri, new CoreTextSpan(literalStart + 3, 0), "f")]);
    }

    [Fact]
    public async Task CodeAction_LazySupported_ReturnsActionWithDataAndResolveAddsEdit()
    {
        await InitializeAsync(lazy: true, documentChanges: true);
        await OpenAsync();
        ConfigureFloatFix();

        CodeActionPayload[]? actions = await _session.Client
            .CodeActionAsync(_workspace.DocumentUri("a.cvl"), Cursor)
            .WithTimeout("codeAction");

        CodeActionPayload action = Assert.Single(actions!);
        Assert.Equal("quickfix", action.Kind);
        Assert.Equal(FixTitle, action.Title);
        Assert.NotNull(action.Data);
        Assert.Null(action.Edit);
        DiagnosticPayload diagnostic = Assert.Single(action.Diagnostics!);
        Assert.Equal("CVL1900", diagnostic.Code);
        Assert.Equal("cvolo", diagnostic.Source);

        var request = new Dictionary<string, object?>
        {
            ["title"] = action.Title,
            ["kind"] = action.Kind,
            ["data"] = action.Data,
        };

        Dictionary<string, object?>? resolved = await _session.Client
            .ResolveCodeActionAsync(request)
            .WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.Equal(FixTitle, resolved!["title"]?.ToString());
        var edit = Assert.IsType<JObject>(resolved["edit"]);
        var documentChanges = Assert.IsType<JArray>(edit["documentChanges"]);
        var textDocumentEdit = Assert.IsType<JObject>(Assert.Single(documentChanges));
        var textEdit = Assert.IsType<JObject>(Assert.Single(Assert.IsType<JArray>(textDocumentEdit["edits"])));
        Assert.Equal("f", textEdit["newText"]!.Value<string>());
        Assert.Equal(2, textEdit["range"]!["start"]!["line"]!.Value<int>());
        Assert.Equal(17, textEdit["range"]!["start"]!["character"]!.Value<int>());
    }

    [Fact]
    public async Task CodeAction_WithoutLazySupport_ReturnsEagerEdit()
    {
        await InitializeAsync(lazy: false, documentChanges: false);
        await OpenAsync();
        ConfigureFloatFix();

        CodeActionPayload[]? actions = await _session.Client
            .CodeActionAsync(_workspace.DocumentUri("a.cvl"), Cursor)
            .WithTimeout("codeAction");

        CodeActionPayload action = Assert.Single(actions!);
        Assert.Null(action.Data);
        Assert.NotNull(action.Edit);
        Assert.Null(action.Edit!.DocumentChanges);
        var changes = action.Edit.Changes;
        Assert.NotNull(changes);
        TextEditPayload[] texts = Assert.Single(changes!).Value;
        TextEditPayload textEdit = Assert.Single(texts);
        Assert.Equal("f", textEdit.NewText);
        Assert.Equal(2, textEdit.Range.Start.Line);
        Assert.Equal(17, textEdit.Range.Start.Character);
    }

    [Fact]
    public async Task CodeAction_OnlyRefactor_ReturnsEmptyWithoutCallingBackend()
    {
        await InitializeAsync(lazy: true);
        await OpenAsync();
        ConfigureFloatFix();

        CodeActionPayload[]? actions = await _session.Client
            .CodeActionAsync(_workspace.DocumentUri("a.cvl"), Cursor, ["refactor"])
            .WithTimeout("codeAction");

        Assert.Empty(actions!);
        Assert.Equal(0, _backend.CodeFixCalls);
    }

    [Fact]
    public async Task CodeAction_NoBackendFixes_ReturnsEmpty()
    {
        await InitializeAsync(lazy: true);
        await OpenAsync();
        _backend.CannedCodeFixes = new BackendCodeFixResult([]);

        CodeActionPayload[]? actions = await _session.Client
            .CodeActionAsync(_workspace.DocumentUri("a.cvl"), Cursor)
            .WithTimeout("codeAction");

        Assert.Empty(actions!);
    }

    [Fact]
    public async Task Resolve_AfterDocumentChange_ReturnsContentModified()
    {
        await InitializeAsync(lazy: true);
        await OpenAsync();
        ConfigureFloatFix();

        CodeActionPayload[]? actions = await _session.Client
            .CodeActionAsync(_workspace.DocumentUri("a.cvl"), Cursor)
            .WithTimeout("codeAction");
        CodeActionPayload action = Assert.Single(actions!);

        var request = new Dictionary<string, object?>
        {
            ["title"] = action.Title,
            ["data"] = action.Data,
        };

        await _session.Client
            .NotifyDidChangeAsync(_workspace.DocumentUri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = DocumentText + "\n" })
            .WithTimeout("didChange");
        await _session.Client.DrainNotificationsAsync().WithTimeout("drain");

        RemoteRpcException error = await _session.Client
            .ExpectErrorAsync("codeAction/resolve", request)
            .WithTimeout("resolve");

        Assert.Equal(-32801, (int?)error.ErrorCode);
    }

    [Fact]
    public async Task Resolve_UnknownToken_ReturnsRequestFailed()
    {
        await InitializeAsync(lazy: true);
        await OpenAsync();

        var request = new Dictionary<string, object?>
        {
            ["title"] = "orphan",
            ["data"] = "not-a-real-token",
        };

        RemoteRpcException error = await _session.Client
            .ExpectErrorAsync("codeAction/resolve", request)
            .WithTimeout("resolve");

        Assert.Equal(-32803, (int?)error.ErrorCode);
    }

    [Fact]
    public async Task Initialize_WithLiteralSupport_AdvertisesQuickFixProviderWithResolve()
    {
        InitializeResponse response = await InitializeAsync(lazy: true);

        var provider = Assert.IsType<JObject>(response.Capabilities.CodeActionProvider);
        Assert.True(provider["resolveProvider"]!.Value<bool>());
        Assert.Equal("quickfix", Assert.Single(provider["codeActionKinds"]!.Values<string>()));
    }

    [Fact]
    public async Task Initialize_WithLiteralSupportButNoLazySupport_AdvertisesEagerProvider()
    {
        InitializeResponse response = await InitializeAsync(lazy: false);

        var provider = Assert.IsType<JObject>(response.Capabilities.CodeActionProvider);
        Assert.False(provider["resolveProvider"]!.Value<bool>());
    }

    [Fact]
    public async Task Initialize_WithoutLiteralSupport_OmitsCodeActionProvider()
    {
        InitializeResponse response = await _session.Client
            .InitializeWithAsync(new InitializeRequestParams
            {
                ProcessId = null,
                RootUri = new Uri(_workspace.DirectoryPath),
                Capabilities = new ClientCapabilitiesPayload(),
            })
            .WithTimeout("initialize");

        Assert.Null(response.Capabilities.CodeActionProvider);
    }
}
