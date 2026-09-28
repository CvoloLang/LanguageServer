using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic coverage of the textDocument/foldingRange transport. The regions are the ones the
/// language defined; the handler only decides which lines a client hides, and a region it cannot map
/// is omitted rather than repaired (§43, §44).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class FoldingRangeHandlerTests : IDisposable
{
    private const string Text = "int main()\n{\n    return 0;\n}\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public FoldingRangeHandlerTests()
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

    [Fact]
    public async Task Region_IsFoldedFromItsFirstToItsLastLine()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges = [new BackendFoldingRange(new TextSpan(0, 29))];

        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange");

        JToken range = Assert.Single(ranges!);
        Assert.Equal(0, range["startLine"]!.Value<int>());
        Assert.Equal(0, range["startCharacter"]!.Value<int>());
        Assert.Equal(3, range["endLine"]!.Value<int>());
        Assert.Null(range["endCharacter"]);
    }

    [Fact]
    public async Task RegionEndingAtTheStartOfALine_EndsOnThePreviousLine()
    {
        await StartAsync();
        await OpenAsync();
        // The block from the opening brace to just before the final newline.
        _backend.CannedFoldingRanges = [new BackendFoldingRange(new TextSpan(11, 18))];

        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange");

        JToken range = Assert.Single(ranges!);
        Assert.Equal(1, range["startLine"]!.Value<int>());
        Assert.Equal(3, range["endLine"]!.Value<int>());
    }

    [Fact]
    public async Task RegionEndingMidLine_KeepsItsEndLine()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges = [new BackendFoldingRange(new TextSpan(0, 20))];

        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange");

        JToken range = Assert.Single(ranges!);
        Assert.Equal(0, range["startLine"]!.Value<int>());
        Assert.Equal(2, range["endLine"]!.Value<int>());
    }

    [Fact]
    public async Task RegionOnASingleLine_IsNotFoldable()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges = [new BackendFoldingRange(new TextSpan(0, 10))];

        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange");

        Assert.Empty(ranges!);
    }

    [Fact]
    public async Task CommentsAndDocumentation_BothBecomeComments_AndPlainRegionsCarryNoKind()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges =
        [
            new BackendFoldingRange(new TextSpan(0, 29), BackendFoldingRangeKind.None),
            new BackendFoldingRange(new TextSpan(11, 18), BackendFoldingRangeKind.Comment),
            new BackendFoldingRange(new TextSpan(13, 15), BackendFoldingRangeKind.Documentation),
        ];

        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange");

        Assert.Equal(3, ranges!.Count);
        Assert.Null(ranges[0]!["kind"]);
        Assert.Equal("comment", ranges[1]!["kind"]!.Value<string>());
        Assert.Equal("comment", ranges[2]!["kind"]!.Value<string>());
    }

    [Fact]
    public async Task RegionOutsideTheText_IsDropped()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges =
        [
            new BackendFoldingRange(new TextSpan(0, 29)),
            new BackendFoldingRange(new TextSpan(9000, 10)),
            new BackendFoldingRange(new TextSpan(-2, 8)),
        ];

        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange");

        Assert.Single(ranges!);
    }

    [Fact]
    public async Task Regions_AreOrderedByStartThenEndLine()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges =
        [
            new BackendFoldingRange(new TextSpan(0, 20)),
            new BackendFoldingRange(new TextSpan(11, 18)),
            new BackendFoldingRange(new TextSpan(0, 29)),
        ];

        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange");

        Assert.Equal(
            [(0, 2), (0, 3), (1, 3)],
            ranges!.Select(range => (range["startLine"]!.Value<int>(), range["endLine"]!.Value<int>())).ToArray());
    }

    [Fact]
    public async Task UnopenedDocument_YieldsNoRegions()
    {
        await StartAsync();

        Assert.Null(await _session.Client.FoldingRangesAsync(Uri).WithTimeout("foldingRange"));
        Assert.Equal(0, _backend.FoldingRangeCalls);
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightRegions()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges = [new BackendFoldingRange(new TextSpan(0, 29))];

        _backend.ArmEditorIntelligence();
        Task<JArray?> inFlight = _session.Client.FoldingRangesAsync(Uri);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "int other()\n{\n}\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == 2,
            "document advanced to v2");
        _backend.ReleaseEditorIntelligence();

        Assert.Null(await inFlight.WithTimeout("stale foldingRange"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedFoldingRanges = [new BackendFoldingRange(new TextSpan(0, 29))];

        _backend.ArmEditorIntelligence();
        using var cts = new CancellationTokenSource();
        Task<JArray?> inFlight = _session.Client.FoldingRangesWithTokenAsync(Uri, cts.Token);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseEditorIntelligence();

        try
        {
            JArray? result = await inFlight.WithTimeout("cancelled foldingRange");
            Assert.Fail($"Expected cancellation, but foldingRange returned {result?.Count} regions.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.FoldingRangesAsync(Uri).WithTimeout("next foldingRange"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync();

        _backend.ThrowOnEditorIntelligence = true;
        Assert.Null(await _session.Client.FoldingRangesAsync(Uri).WithTimeout("failing foldingRange"));

        _backend.ThrowOnEditorIntelligence = false;
        _backend.CannedFoldingRanges = [new BackendFoldingRange(new TextSpan(0, 29))];
        JArray? ranges = await _session.Client.FoldingRangesAsync(Uri).WithTimeout("next foldingRange");
        Assert.Single(ranges!);
    }
}
