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
/// Deterministic staleness/cancellation/failure coverage for hover, definition
/// and document symbols, using an explicit blocking fake backend (§28.3, §28.4,
/// §28.10, §28.12, §28.17, §28.18).
/// </summary>
public class NavigationConcurrencyTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public NavigationConcurrencyTests()
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

    private Uri Uri(string name) => _workspace.DocumentUri(name);

    private async Task StartAsync()
    {
        await _session.Client.InitializeAsync().WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(string name, int version, string text)
    {
        await _session.Client.NotifyDidOpenAsync(Uri(name), "cvolo", version, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private async Task WaitForVersionAsync(string name, int version)
    {
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == version,
            $"{name} advanced to v{version}");
    }

    private static BackendSymbolInfo Symbol() =>
        new(new FakeSymbolHandle(), new TextSpan(0, 4), "main", BackendSymbolKind.Function, "int main()");

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightHover()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        _backend.CannedSymbol = Symbol();

        _backend.ArmNavigation();
        Task<Hover?> inFlight = _session.Client.HoverAsync(Uri("a.cvl"), 0, 4);
        Assert.True(_backend.WaitUntilNavigationEntered(TimeSpan.FromSeconds(5)), "backend navigation should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        await WaitForVersionAsync("a.cvl", 2);
        _backend.ReleaseNavigation();

        Assert.Null(await inFlight.WithTimeout("stale hover"));
    }

    [Fact]
    public async Task CrossFileEdit_DiscardsInFlightDefinition()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        await OpenAsync("b.cvl", 1, "int other() { return 0; }\n");
        _backend.CannedSymbol = Symbol();

        _backend.ArmNavigation();
        Task<Location[]?> inFlight = _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4);
        Assert.True(_backend.WaitUntilNavigationEntered(TimeSpan.FromSeconds(5)), "backend navigation should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri("b.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int other() { return 1; }\n" });
        await WaitForVersionAsync("b.cvl", 2);
        _backend.ReleaseNavigation();

        Assert.Null(await inFlight.WithTimeout("stale definition"));
    }

    [Fact]
    public async Task CloseReopen_DiscardsInFlightDocumentSymbols()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        _backend.CannedSymbols =
        [
            new BackendDocumentSymbol("main", null, BackendSymbolKind.Function, new TextSpan(0, 4), new TextSpan(4, 4), []),
        ];

        _backend.ArmNavigation();
        Task<JArray?> inFlight = _session.Client.DocumentSymbolAsync(Uri("a.cvl"));
        Assert.True(_backend.WaitUntilNavigationEntered(TimeSpan.FromSeconds(5)), "backend navigation should be entered");

        await _session.Client.NotifyDidCloseAsync(Uri("a.cvl"));
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => !_store.TryGet(coreUri, out _), "didClose applied");
        await OpenAsync("a.cvl", 1, "int main() { return 2; }\n");
        _backend.ReleaseNavigation();

        Assert.Null(await inFlight.WithTimeout("old-lifetime documentSymbol"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        _backend.CannedSymbol = Symbol();

        _backend.ArmNavigation();
        using var cts = new CancellationTokenSource();
        Task<Hover?> inFlight = _session.Client.HoverWithTokenAsync(Uri("a.cvl"), 0, 4, cts.Token);
        Assert.True(_backend.WaitUntilNavigationEntered(TimeSpan.FromSeconds(5)), "backend navigation should be entered");

        cts.Cancel();
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseNavigation();

        try
        {
            Hover? result = await inFlight.WithTimeout("cancelled hover");
            Assert.Fail($"Expected cancellation, but hover returned '{result?.Contents.Value}'.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.HoverAsync(Uri("a.cvl"), 0, 4).WithTimeout("next hover"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextHoverSucceeds()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _backend.ThrowOnNavigation = true;
        Assert.Null(await _session.Client.HoverAsync(Uri("a.cvl"), 0, 4).WithTimeout("failing hover"));

        _backend.ThrowOnNavigation = false;
        _backend.CannedSymbol = Symbol();
        Assert.NotNull(await _session.Client.HoverAsync(Uri("a.cvl"), 0, 4).WithTimeout("next hover"));
    }

    [Fact]
    public async Task Definition_MalformedTarget_IsSkipped_NotClamped()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        const string text = "int main() { return 0; }\n";

        _backend.CannedSymbol = Symbol();
        _backend.CannedDefinitions = new BackendDefinitionResult(
            new Dictionary<DocumentUri, string> { [coreUri] = text },
            [
                new BackendDefinitionTarget(coreUri, new TextSpan(0, 3), new TextSpan(0, 5000)),
                new BackendDefinitionTarget(coreUri, new TextSpan(0, 3), new TextSpan(4, 4)),
            ]);

        Location[]? locations = await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4).WithTimeout("definition");

        Assert.NotNull(locations);
        Location location = Assert.Single(locations!);
        Assert.Equal(4, location.Range.Start.Character);
        Assert.Equal(8, location.Range.End.Character);
    }

    private sealed class FakeSymbolHandle : BackendSymbolHandle
    {
    }
}
