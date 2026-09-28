using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic coverage of the textDocument/codeLens transport: ranges, command arguments, the
/// presentation order, the refusal to invent a position, and the usual staleness, cancellation and
/// failure containment (§83).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class CodeLensHandlerTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public CodeLensHandlerTests()
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

    private Uri Uri => _workspace.DocumentUri("a.cvl");

    private async Task StartAsync()
    {
        await _session.Client.InitializeAsync().WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(int version = 1, string text = Text)
    {
        await _session.Client.NotifyDidOpenAsync(Uri, "cvolo", version, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private static BackendCodeLensInfo References(int start, int length, string title) =>
        new(
            new TextSpan(start, length),
            BackendCodeLensKind.References,
            title,
            new BackendCodeLensCommand("cvolo.showReferences", [new BackendCodeLensPositionArgument(start)]));

    [Fact]
    public async Task Lenses_MapRangeAndCommandWithTheDocumentUriAndDeclarationPosition()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal(0, lens["range"]!["start"]!["line"]!.Value<int>());
        Assert.Equal(4, lens["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(0, lens["range"]!["end"]!["line"]!.Value<int>());
        Assert.Equal(8, lens["range"]!["end"]!["character"]!.Value<int>());

        Assert.Equal("3 references", lens["command"]!["title"]!.Value<string>());
        Assert.Equal("cvolo.showReferences", lens["command"]!["command"]!.Value<string>());

        JArray arguments = Assert.IsType<JArray>(lens["command"]!["arguments"]!);
        Assert.Equal(2, arguments.Count);
        Assert.Equal(_workspace.PathOf("a.cvl"), arguments[0]!.Value<string>());
        Assert.Equal(0, arguments[1]!["line"]!.Value<int>());
        Assert.Equal(4, arguments[1]!["character"]!.Value<int>());
    }

    [Fact]
    public async Task Lenses_AreOrderedReferencesThenLayoutThenNativeInterop()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            new BackendCodeLensInfo(new TextSpan(4, 4), BackendCodeLensKind.NativeInterop, "native"),
            new BackendCodeLensInfo(new TextSpan(4, 4), BackendCodeLensKind.Layout, "size 24 B"),
            References(4, 4, "1 reference"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        Assert.Equal(
            ["1 reference", "size 24 B", "native"],
            lenses!.Select(lens => lens["command"]!["title"]!.Value<string>()).ToArray());
        Assert.Equal(
            ["cvolo.showReferences", string.Empty, string.Empty],
            lenses!.Select(lens => lens["command"]!["command"]!.Value<string>()).ToArray());
    }

    [Fact]
    public async Task Lenses_AreOrderedByPositionRegardlessOfBackendEnumerationOrder()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            References(18, 1, "2 references"),
            References(4, 4, "3 references"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        Assert.Equal(
            [4, 18],
            lenses!.Select(lens => lens["range"]!["start"]!["character"]!.Value<int>()).ToArray());
    }

    [Fact]
    public async Task LensWithoutACommand_StillCarriesTheCompilerText()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            new BackendCodeLensInfo(new TextSpan(4, 4), BackendCodeLensKind.NativeInterop, "import kernel32 · CreateFileW"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal("import kernel32 · CreateFileW", lens["command"]!["title"]!.Value<string>());
        Assert.Equal(string.Empty, lens["command"]!["command"]!.Value<string>());
    }

    [Fact]
    public async Task LensOutsideTheCapturedText_IsDropped_RangeIsNeverClamped()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            References(4, 4, "3 references"),
            References(9000, 4, "0 references"),
            References(-3, 2, "negative"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        Assert.Equal("3 references", Assert.Single(lenses!)["command"]!["title"]!.Value<string>());
    }

    [Fact]
    public async Task LensWhoseCommandPositionDoesNotMap_KeepsItsTextAndLosesItsCommand()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            new BackendCodeLensInfo(
                new TextSpan(4, 4),
                BackendCodeLensKind.References,
                "3 references",
                new BackendCodeLensCommand("cvolo.showReferences", [new BackendCodeLensPositionArgument(9000)])),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal("3 references", lens["command"]!["title"]!.Value<string>());
        Assert.Equal(string.Empty, lens["command"]!["command"]!.Value<string>());
    }

    [Fact]
    public async Task UnopenedDocument_YieldsNoLenses()
    {
        await StartAsync();

        Assert.Null(await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens"));
        Assert.Equal(0, _backend.CodeLensCalls);
    }

    [Fact]
    public async Task Settings_AreForwardedToTheBackend()
    {
        await StartAsync();
        await OpenAsync();

        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        BackendCodeLensOptions? options = _backend.LastCodeLensOptions;
        Assert.NotNull(options);
        Assert.True(options!.References);
        Assert.True(options.Layout);
        Assert.False(options.Members);
        Assert.True(options.NativeInterop);

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject
            {
                ["cvolo.codeLens.references"] = false,
                ["cvolo.codeLens.members"] = true,
            })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();
        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");

        options = _backend.LastCodeLensOptions;
        Assert.NotNull(options);
        Assert.False(options!.References);
        Assert.True(options.Layout);
        Assert.True(options.Members);
        Assert.True(options.NativeInterop);
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightLenses()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];

        _backend.ArmEditorIntelligence();
        Task<JArray?> inFlight = _session.Client.CodeLensAsync(Uri);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == 2,
            "document advanced to v2");
        _backend.ReleaseEditorIntelligence();

        Assert.Null(await inFlight.WithTimeout("stale codeLens"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];

        _backend.ArmEditorIntelligence();
        using var cts = new CancellationTokenSource();
        Task<JArray?> inFlight = _session.Client.CodeLensWithTokenAsync(Uri, cts.Token);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseEditorIntelligence();

        try
        {
            JArray? result = await inFlight.WithTimeout("cancelled codeLens");
            Assert.Fail($"Expected cancellation, but codeLens returned {result?.Count} lenses.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.CodeLensAsync(Uri).WithTimeout("next codeLens"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync();

        _backend.ThrowOnEditorIntelligence = true;
        Assert.Null(await _session.Client.CodeLensAsync(Uri).WithTimeout("failing codeLens"));

        _backend.ThrowOnEditorIntelligence = false;
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];
        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("next codeLens");
        Assert.Single(lenses!);
    }
}
