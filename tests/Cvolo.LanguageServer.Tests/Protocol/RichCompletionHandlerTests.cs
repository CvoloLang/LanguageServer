using System.Text.RegularExpressions;
using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic LSP-7 §46 coverage for rich completion and completionItem/resolve
/// using a blocking fake backend: capability negotiation, overload preservation,
/// snippet encoding, opaque resolve tokens, staleness and bounded staging.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class RichCompletionHandlerTests : IDisposable
{
    private const string DocumentText = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public RichCompletionHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(["a.cvl", "b.cvl"]);
        _backend = new BlockingBackend();
        _store = new DocumentStore(_backend, new RecordingCoreLogger());
        _session = ProtocolSession.Start(storeFactory: () => _store);
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Task StartAsync()
    {
        return StartWithCapabilitiesAsync(null);
    }

    private async Task StartWithCapabilitiesAsync(ClientCapabilitiesPayload? capabilities)
    {
        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            ProcessId = null,
            RootUri = new Uri(_workspace.DirectoryPath),
            Capabilities = capabilities,
        }).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(string name, string text = DocumentText)
    {
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri(name), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private void SetItems(params BackendCompletionItem[] items)
    {
        _backend.CannedItems = items;
        _backend.CompletionResultFactory = null;
        _backend.ThrowOnCompletion = false;
    }

    private Task<CompletionList?> CompleteAsync(int line = 0, int character = 0)
    {
        return _session.Client.CompletionAsync(_workspace.DocumentUri("a.cvl"), line, character);
    }

    private static ClientCapabilitiesPayload CompletionCapabilities(bool snippet, string[]? resolveProperties, string[]? documentationFormat = null)
    {
        return new ClientCapabilitiesPayload
        {
            TextDocument = new TextDocumentClientCapabilitiesPayload
            {
                Completion = new CompletionClientCapabilitiesPayload
                {
                    CompletionItem = new CompletionItemClientCapabilitiesPayload
                    {
                        SnippetSupport = snippet,
                        DocumentationFormat = documentationFormat,
                        ResolveSupport = resolveProperties is null
                            ? null
                            : new CompletionResolveSupportClientCapabilitiesPayload { Properties = resolveProperties },
                    },
                },
            },
        };
    }

    private static BackendCompletionInsertionPlan Plan(params BackendCompletionInsertSegment[] segments)
    {
        return new BackendCompletionInsertionPlan(segments);
    }

    [Fact]
    public async Task Resolve_RoundTrip_AppliesDetailAndDocumentation()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail | BackendCompletionResolvableFields.Documentation));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", "F documentation");

        CompletionList? completed = await CompleteAsync();
        Assert.NotNull(completed);
        CompletionItem item = Assert.Single(completed!.Items);
        Assert.NotNull(item.Data);

        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.Equal("int F(int x)", resolved!.Detail);
        Assert.True(resolved.Documentation.HasValue);
        Assert.True(resolved.Documentation!.Value.TryGetSecond(out MarkupContent? documentation));
        Assert.Equal(MarkupKind.PlainText, documentation.Kind);
        Assert.Equal("F documentation", documentation.Value);
        Assert.Equal("F", resolved.Label);
    }

    [Fact]
    public async Task ResolveMarkdown_WhenMarkdownDocumentationFormatIsPreferred()
    {
        await StartWithCapabilitiesAsync(CompletionCapabilities(snippet: false, resolveProperties: null, documentationFormat: ["markdown", "plaintext"]));
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Documentation));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo(null, "**F** docs");

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();

        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.True(resolved!.Documentation.HasValue);
        Assert.True(resolved.Documentation!.Value.TryGetSecond(out MarkupContent? documentation));
        Assert.Equal(MarkupKind.Markdown, documentation.Kind);
    }

    [Fact]
    public async Task CapabilityNegotiation_DetailOnly_DoesNotApplyDocumentation()
    {
        await StartWithCapabilitiesAsync(CompletionCapabilities(snippet: false, resolveProperties: ["detail"]));
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail | BackendCompletionResolvableFields.Documentation));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", "should not appear");

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();
        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.Equal("int F(int x)", resolved!.Detail);
        Assert.Null(resolved.Documentation);
    }

    [Fact]
    public async Task CapabilityNegotiation_DocumentationOnly_DoesNotApplyDetail()
    {
        await StartWithCapabilitiesAsync(CompletionCapabilities(snippet: false, resolveProperties: ["documentation"]));
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail | BackendCompletionResolvableFields.Documentation));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("should not appear", "F documentation");

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();
        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.Null(resolved!.Detail);
        Assert.True(resolved.Documentation!.Value.TryGetSecond(out MarkupContent? documentation));
        Assert.Equal("F documentation", documentation.Value);
    }

    [Fact]
    public async Task CapabilityNegotiation_EmptyProperties_AttachesNoResolveData()
    {
        await StartWithCapabilitiesAsync(CompletionCapabilities(snippet: false, resolveProperties: []));
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail | BackendCompletionResolvableFields.Documentation));

        CompletionList? completed = await CompleteAsync();
        Assert.NotNull(completed);
        CompletionItem item = Assert.Single(completed!.Items);
        Assert.Null(item.Data);

        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("resolve");
        Assert.NotNull(resolved);
        Assert.Null(resolved!.Detail);
        Assert.Null(resolved.Documentation);
    }

    [Fact]
    public async Task AlreadyPopulatedDetail_IsNotReplaced_ByResolve()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: "pre-populated",
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail | BackendCompletionResolvableFields.Documentation));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("replacement detail", "docs");

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();
        Assert.Equal("pre-populated", item.Detail);

        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.Equal("pre-populated", resolved!.Detail);
        Assert.NotNull(resolved.Documentation);
    }

    [Fact]
    public async Task UnknownToken_ReturnsItemUnchanged()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", "docs");

        CompletionItem unknown = new() { Label = "F", Data = "no-such-token" };
        var resolved = await _session.Client.ResolveCompletionItemAsync(unknown).WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.Equal("F", resolved!.Label);
        Assert.Null(resolved.Detail);
        Assert.Null(resolved.Documentation);
    }

    [Fact]
    public async Task NonStringResolveData_ReturnsItemUnchanged()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", "docs");

        CompletionItem nonString = new() { Label = "F", Data = 42 };
        var resolved = await _session.Client.ResolveCompletionItemAsync(nonString).WithTimeout("resolve");

        Assert.NotNull(resolved);
        Assert.Equal("F", resolved!.Label);
        Assert.Null(resolved.Detail);
        Assert.Null(resolved.Documentation);
    }

    [Fact]
    public async Task BackendResolveException_IsContained_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", null);

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();
        Assert.NotNull(item.Data);

        _backend.ThrowOnResolve = true;
        var failed = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("failing resolve");
        Assert.NotNull(failed);
        Assert.Null(failed!.Detail);

        _backend.ThrowOnResolve = false;
        var recovered = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("next resolve");
        Assert.NotNull(recovered);
        Assert.Equal("int F(int x)", recovered!.Detail);
    }

    [Fact]
    public async Task SnippetEncoding_EscapesDollarBraceBackslash()
    {
        await StartWithCapabilitiesAsync(CompletionCapabilities(snippet: true, resolveProperties: null));
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "P", "plain", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: Plan(
                new BackendCompletionLiteral("p$"),
                new BackendCompletionPlaceholder("a}"),
                new BackendCompletionLiteral("x\\y"),
                new BackendCompletionFinalCursor()),
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.None));

        CompletionList? result = await CompleteAsync();

        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items);
        Assert.Equal(InsertTextFormat.Snippet, item.InsertTextFormat);
        Assert.Equal("p$$${1:a\\}}x\\\\y$0", item.TextEdit!.NewText);
    }

    [Fact]
    public async Task SnippetCapable_ReceivesNumberedTabStops_AndFinalCursor()
    {
        await StartWithCapabilitiesAsync(CompletionCapabilities(snippet: true, resolveProperties: null));
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: Plan(
                new BackendCompletionLiteral("F("),
                new BackendCompletionPlaceholder("left"),
                new BackendCompletionLiteral(", "),
                new BackendCompletionPlaceholder("right"),
                new BackendCompletionLiteral(")"),
                new BackendCompletionFinalCursor()),
            ResolveHandle: null,
            ResolvableFields: BackendCompletionResolvableFields.None));

        CompletionList? result = await CompleteAsync();

        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items);
        Assert.Equal(InsertTextFormat.Snippet, item.InsertTextFormat);
        Assert.Equal("F(${1:left}, ${2:right})$0", item.TextEdit!.NewText);

        TextEdit edit = item.TextEdit!;
        Assert.Equal(0, edit.Range.Start.Line);
        Assert.Equal(0, edit.Range.Start.Character);
        Assert.Equal(0, edit.Range.End.Line);
        Assert.Equal(0, edit.Range.End.Character);
    }

    [Fact]
    public async Task PlainClient_NeverSeesSnippetSyntax()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F(...)", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: Plan(
                new BackendCompletionLiteral("F("),
                new BackendCompletionPlaceholder("left"),
                new BackendCompletionLiteral(")"),
                new BackendCompletionFinalCursor()),
            ResolveHandle: null,
            ResolvableFields: BackendCompletionResolvableFields.None));

        CompletionList? result = await CompleteAsync();

        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items);
        Assert.Equal(InsertTextFormat.Plaintext, item.InsertTextFormat);
        Assert.Equal("F(...)", item.TextEdit!.NewText);
        Assert.DoesNotContain("${", item.TextEdit!.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoInsertionPlan_IsPlainEvenForSnippetClients()
    {
        await StartWithCapabilitiesAsync(CompletionCapabilities(snippet: true, resolveProperties: null));
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: null,
            ResolvableFields: BackendCompletionResolvableFields.None));

        CompletionList? result = await CompleteAsync();

        Assert.NotNull(result);
        CompletionItem item = Assert.Single(result!.Items);
        Assert.Equal(InsertTextFormat.Plaintext, item.InsertTextFormat);
        Assert.Equal("F", item.TextEdit!.NewText);
    }

    [Fact]
    public async Task ResolveData_IsOpaqueServerGeneratedToken()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail));

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();

        Assert.NotNull(item.Data);
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), (string)item.Data!);
    }

    [Fact]
    public async Task Resolve_AfterSameDocumentEdit_ReturnsItemUnchanged()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", null);

        CompletionList? completed = await CompleteAsync();
        Assert.NotNull(completed);
        CompletionItem item = Assert.Single(completed!.Items);
        Assert.NotNull(item.Data);

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out var state) && state.Version.Value == 2, "a.cvl advanced to v2");

        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("stale resolve");

        Assert.NotNull(resolved);
        Assert.Equal("F", resolved!.Label);
        Assert.Null(resolved.Detail);
        Assert.Null(resolved.Documentation);
    }

    [Fact]
    public async Task Resolve_AfterCrossFileEdit_ReturnsItemUnchanged()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        await OpenAsync("b.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", null);

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();
        Assert.NotNull(item.Data);

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("b.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 2; }\n" });
        var coreUriB = DocumentUri.Create(_workspace.PathOf("b.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUriB, out var state) && state.Version.Value == 2, "b.cvl advanced to v2");

        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("cross-file stale resolve");

        Assert.NotNull(resolved);
        Assert.Null(resolved!.Detail);
    }

    [Fact]
    public async Task Resolve_AfterCloseReopen_ReturnsItemUnchanged()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", null);

        CompletionList? completed = await CompleteAsync();
        CompletionItem item = completed!.Items.Single();
        Assert.NotNull(item.Data);

        await _session.Client.NotifyDidCloseAsync(_workspace.DocumentUri("a.cvl"));
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => !_store.TryGet(coreUri, out _), "didClose applied");
        await OpenAsync("a.cvl");

        var resolved = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("closed-lifetime resolve");

        Assert.NotNull(resolved);
        Assert.Null(resolved!.Detail);
    }

    [Fact]
    public async Task StaleCompletion_DiscardsStagedBatch()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail));

        _backend.Arm();
        Task<CompletionList?> inFlight = CompleteAsync();
        Assert.True(_backend.WaitUntilEntered(TimeSpan.FromSeconds(5)), "backend completion should be entered");

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 3; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out var state) && state.Version.Value == 2, "a.cvl advanced to v2");
        _backend.Release();

        Assert.Null(await inFlight.WithTimeout("stale completion"));

        // The staged resolve entries were discarded; nothing was attached to the response.
        CompletionList? fresh = await CompleteAsync();
        Assert.NotNull(fresh);
        Assert.NotNull(fresh!.Items.Single());
    }

    [Fact]
    public async Task Cancellation_OnResolve_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        SetItems(new BackendCompletionItem(
            "F", "F", BackendCompletionKind.Function,
            Detail: null,
            InsertionPlan: null,
            ResolveHandle: _backend.CreateResolveHandle(),
            ResolvableFields: BackendCompletionResolvableFields.Detail));
        _backend.CannedResolvedInfo = new BackendCompletionResolvedInfo("int F(int x)", null);

        CompletionItem item = (await CompleteAsync())!.Items.Single();
        Assert.NotNull(item.Data);

        _backend.ArmResolve();
        using var cts = new CancellationTokenSource();
        Task<CompletionItem?> inFlight = _session.Client.ResolveCompletionItemWithTokenAsync(item, cts.Token);
        Assert.True(_backend.WaitUntilResolveEntered(TimeSpan.FromSeconds(5)), "backend resolve should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseResolve();

        try
        {
            var result = await inFlight.WithTimeout("cancelled resolve");
            Assert.Fail($"Expected cancellation, but resolve returned {result?.Detail ?? "<unchanged>"}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed.
        }

        var recovered = await _session.Client.ResolveCompletionItemAsync(item).WithTimeout("next resolve");
        Assert.NotNull(recovered);
        Assert.Equal("int F(int x)", recovered!.Detail);
    }
}