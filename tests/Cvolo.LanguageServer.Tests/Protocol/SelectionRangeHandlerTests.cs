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
/// Deterministic coverage of the textDocument/selectionRange transport: one chain per requested
/// cursor position, innermost level first, and a chain the server cannot vouch for is dropped whole
/// rather than repaired (§45, §81, §85).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class SelectionRangeHandlerTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public SelectionRangeHandlerTests()
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

    /// <summary>The literal "0" inside the body, then the "return 0" expression, then the whole body.</summary>
    private static BackendSelectionRange Chain() => new(
        new TextSpan(20, 1),
        new BackendSelectionRange(
            new TextSpan(13, 9),
            new BackendSelectionRange(new TextSpan(11, 13), null)));


    /// <summary>
    /// A position the server could not answer keeps its place in the list as a JSON null, so the
    /// chains a client receives stay aligned with the positions it asked about (§45).
    /// </summary>
    private static void AssertHole(JToken? token)
    {
        Assert.NotNull(token);
        Assert.Equal(JTokenType.Null, token!.Type);
    }

    [Fact]
    public async Task Chain_IsReturnedInnermostFirst_WithParentsGrowingOutward()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSelectionRanges = [Chain()];

        JArray? chains = await _session.Client.SelectionRangesAsync(Uri, (0, 20)).WithTimeout("selectionRange");

        JToken innermost = Assert.Single(chains!);

        Assert.Equal(20, innermost["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(21, innermost["range"]!["end"]!["character"]!.Value<int>());

        JToken expression = innermost["parent"]!;
        Assert.Equal(13, expression["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(22, expression["range"]!["end"]!["character"]!.Value<int>());

        JToken body = expression["parent"]!;
        Assert.Equal(11, body["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(24, body["range"]!["end"]!["character"]!.Value<int>());

        Assert.Null(body["parent"]);
    }

    [Fact]
    public async Task ChainWhoseParentDoesNotStrictlyContainIt_IsDroppedWhole()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSelectionRanges =
        [
            // The parent repeats the level, so the chain would offer a selection step that changes
            // nothing.
            new BackendSelectionRange(new TextSpan(20, 1), new BackendSelectionRange(new TextSpan(20, 1), null)),
        ];

        JArray? chains = await _session.Client.SelectionRangesAsync(Uri, (0, 20)).WithTimeout("selectionRange");

        AssertHole(Assert.Single(chains!));
    }

    [Fact]
    public async Task ChainWhoseLevelDoesNotMapToTheText_IsDroppedWhole()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSelectionRanges =
        [
            new BackendSelectionRange(
                new TextSpan(20, 1),
                new BackendSelectionRange(new TextSpan(9000, 4), null)),
        ];

        JArray? chains = await _session.Client.SelectionRangesAsync(Uri, (0, 20)).WithTimeout("selectionRange");

        AssertHole(Assert.Single(chains!));
    }

    [Fact]
    public async Task OneChainPerRequestedPosition_WithNothingInventedForTheRest()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSelectionRanges = [Chain(), null];

        JArray? chains = await _session.Client
            .SelectionRangesAsync(Uri, (0, 20), (0, 4))
            .WithTimeout("selectionRange");

        Assert.Equal(2, chains!.Count);
        Assert.NotNull(chains[0]);
        AssertHole(chains[1]);
    }

    [Fact]
    public async Task RequestedPositions_AreForwardedAsOffsets()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSelectionRanges = [Chain(), Chain()];

        await _session.Client.SelectionRangesAsync(Uri, (0, 4), (0, 20)).WithTimeout("selectionRange");

        Assert.Equal([4, 20], _backend.LastSelectionPositions.ToArray());
    }

    [Fact]
    public async Task PositionThatDoesNotMapToTheText_YieldsNoChains()
    {
        await StartAsync();
        await OpenAsync();

        Assert.Null(await _session.Client.SelectionRangesAsync(Uri, (0, 4), (9, 0)).WithTimeout("selectionRange"));
        Assert.Equal(0, _backend.SelectionRangeCalls);
    }

    [Fact]
    public async Task UnopenedDocument_YieldsNoChains()
    {
        await StartAsync();

        Assert.Null(await _session.Client.SelectionRangesAsync(Uri, (0, 4)).WithTimeout("selectionRange"));
        Assert.Equal(0, _backend.SelectionRangeCalls);
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightChains()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSelectionRanges = [Chain()];

        _backend.ArmEditorIntelligence();
        Task<JArray?> inFlight = _session.Client.SelectionRangesAsync(Uri, (0, 20));
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == 2,
            "document advanced to v2");
        _backend.ReleaseEditorIntelligence();

        Assert.Null(await inFlight.WithTimeout("stale selectionRange"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSelectionRanges = [Chain()];

        _backend.ArmEditorIntelligence();
        using var cts = new CancellationTokenSource();
        Task<JArray?> inFlight = _session.Client.SelectionRangesWithTokenAsync(Uri, cts.Token, (0, 20));
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseEditorIntelligence();

        try
        {
            JArray? result = await inFlight.WithTimeout("cancelled selectionRange");
            Assert.Fail($"Expected cancellation, but selectionRange returned {result?.Count} chains.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.SelectionRangesAsync(Uri, (0, 20)).WithTimeout("next selectionRange"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync();

        _backend.ThrowOnEditorIntelligence = true;
        Assert.Null(await _session.Client.SelectionRangesAsync(Uri, (0, 20)).WithTimeout("failing selectionRange"));

        _backend.ThrowOnEditorIntelligence = false;
        _backend.CannedSelectionRanges = [Chain()];
        JArray? chains = await _session.Client.SelectionRangesAsync(Uri, (0, 20)).WithTimeout("next selectionRange");
        Assert.NotNull(Assert.Single(chains!));
    }
}
