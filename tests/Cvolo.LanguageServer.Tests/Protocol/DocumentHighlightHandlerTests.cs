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
/// Deterministic coverage of the textDocument/documentHighlight transport. Highlighting is
/// semantic: the occurrences come from the compiler for one document, and the protocol kind never
/// claims more than the compiler described (§78, §79, §85).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class DocumentHighlightHandlerTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public DocumentHighlightHandlerTests()
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
    public async Task Highlights_CarryTheOccurrenceRangeAndTheMappedAccessKind()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedHighlights =
        [
            new BackendDocumentHighlight(new TextSpan(4, 4), BackendReferenceAccessKind.Declaration),
            new BackendDocumentHighlight(new TextSpan(20, 1), BackendReferenceAccessKind.Read),
            new BackendDocumentHighlight(new TextSpan(21, 1), BackendReferenceAccessKind.Write),
            new BackendDocumentHighlight(new TextSpan(18, 1), BackendReferenceAccessKind.ReadWrite),
        ];

        JArray? highlights = await _session.Client.DocumentHighlightsAsync(Uri, 0, 4).WithTimeout("documentHighlight");

        Assert.Equal(4, highlights!.Count);
        Assert.Equal(4, highlights[0]!["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(8, highlights[0]!["range"]!["end"]!["character"]!.Value<int>());

        // The protocol numbers its highlight kinds: 2 is a read and 3 a write. Plain text carries no
        // kind at all, which is what a client reads as text (§79).
        Assert.Null(highlights[0]!["kind"]);
        Assert.Equal(2, highlights[1]!["kind"]!.Value<int>());
        Assert.Equal(3, highlights[2]!["kind"]!.Value<int>());
        Assert.Null(highlights[3]!["kind"]);

    }

    [Fact]
    public async Task OccurrenceThatIsNotInsideOneLine_IsDropped_NotRepaired()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedHighlights =
        [
            new BackendDocumentHighlight(new TextSpan(4, 4), BackendReferenceAccessKind.Read),
            // The closing brace and the line terminator after it: an occurrence never spans lines.
            new BackendDocumentHighlight(new TextSpan(23, 2), BackendReferenceAccessKind.Read),
            new BackendDocumentHighlight(new TextSpan(9000, 4), BackendReferenceAccessKind.Read),
        ];

        JArray? highlights = await _session.Client.DocumentHighlightsAsync(Uri, 0, 4).WithTimeout("documentHighlight");

        JToken highlight = Assert.Single(highlights!);
        Assert.Equal(4, highlight["range"]!["start"]!["character"]!.Value<int>());
    }

    [Fact]
    public async Task PositionThatDoesNotMapToTheText_YieldsNoHighlights()
    {
        await StartAsync();
        await OpenAsync();

        Assert.Null(await _session.Client.DocumentHighlightsAsync(Uri, 9, 0).WithTimeout("documentHighlight"));
        Assert.Equal(0, _backend.DocumentHighlightCalls);
    }

    [Fact]
    public async Task UnopenedDocument_YieldsNoHighlights()
    {
        await StartAsync();

        Assert.Null(await _session.Client.DocumentHighlightsAsync(Uri, 0, 4).WithTimeout("documentHighlight"));
        Assert.Equal(0, _backend.DocumentHighlightCalls);
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightHighlights()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedHighlights = [new BackendDocumentHighlight(new TextSpan(4, 4), BackendReferenceAccessKind.Read)];

        _backend.ArmEditorIntelligence();
        Task<JArray?> inFlight = _session.Client.DocumentHighlightsAsync(Uri, 0, 4);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == 2,
            "document advanced to v2");
        _backend.ReleaseEditorIntelligence();

        Assert.Null(await inFlight.WithTimeout("stale documentHighlight"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedHighlights = [new BackendDocumentHighlight(new TextSpan(4, 4), BackendReferenceAccessKind.Read)];

        _backend.ArmEditorIntelligence();
        using var cts = new CancellationTokenSource();
        Task<JArray?> inFlight = _session.Client.DocumentHighlightsWithTokenAsync(Uri, 0, 4, cts.Token);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseEditorIntelligence();

        try
        {
            JArray? result = await inFlight.WithTimeout("cancelled documentHighlight");
            Assert.Fail($"Expected cancellation, but documentHighlight returned {result?.Count} highlights.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.DocumentHighlightsAsync(Uri, 0, 4).WithTimeout("next documentHighlight"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync();

        _backend.ThrowOnEditorIntelligence = true;
        Assert.Null(await _session.Client.DocumentHighlightsAsync(Uri, 0, 4).WithTimeout("failing documentHighlight"));

        _backend.ThrowOnEditorIntelligence = false;
        _backend.CannedHighlights = [new BackendDocumentHighlight(new TextSpan(4, 4), BackendReferenceAccessKind.Read)];
        JArray? highlights = await _session.Client.DocumentHighlightsAsync(Uri, 0, 4).WithTimeout("next documentHighlight");
        Assert.Single(highlights!);
    }
}
