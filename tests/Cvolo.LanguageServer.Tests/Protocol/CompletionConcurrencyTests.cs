using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic staleness/cancellation/failure coverage for completion. Every
/// race is created with an explicit blocking fake backend rather than sleeps.
/// </summary>
public class CompletionConcurrencyTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public CompletionConcurrencyTests()
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

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightCompletion()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _backend.Arm();
        Task<CompletionList?> inFlight = _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0);
        Assert.True(_backend.WaitUntilEntered(TimeSpan.FromSeconds(5)), "backend completion should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        await WaitForVersionAsync("a.cvl", 2);
        _backend.Release();

        Assert.Null(await inFlight.WithTimeout("stale completion"));

        CompletionList? next = await _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0).WithTimeout("next completion");
        Assert.NotNull(next);
        Assert.Equal("canned", next!.Items[0].Label);
    }

    [Fact]
    public async Task CrossFileEdit_DiscardsInFlightCompletion()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        await OpenAsync("b.cvl", 1, "int other() { return 0; }\n");

        _backend.Arm();
        Task<CompletionList?> inFlight = _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0);
        Assert.True(_backend.WaitUntilEntered(TimeSpan.FromSeconds(5)), "backend completion should be entered");

        // A's text and version are unchanged; only the shared project generation advances.
        await _session.Client.NotifyDidChangeAsync(Uri("b.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int other() { return 1; }\n" });
        await WaitForVersionAsync("b.cvl", 2);
        _backend.Release();

        Assert.Null(await inFlight.WithTimeout("cross-file stale completion"));
        Assert.NotNull(await _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0).WithTimeout("next completion"));
    }

    [Fact]
    public async Task CloseReopen_DiscardsInFlightCompletion()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _backend.Arm();
        Task<CompletionList?> inFlight = _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0);
        Assert.True(_backend.WaitUntilEntered(TimeSpan.FromSeconds(5)), "backend completion should be entered");

        await _session.Client.NotifyDidCloseAsync(Uri("a.cvl"));
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => !_store.TryGet(coreUri, out _), "didClose applied");
        await OpenAsync("a.cvl", 1, "int main() { return 2; }\n");
        _backend.Release();

        Assert.Null(await inFlight.WithTimeout("old-lifetime completion"));
        Assert.NotNull(await _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0).WithTimeout("next completion"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _backend.Arm();
        using var cts = new CancellationTokenSource();
        Task<CompletionList?> inFlight = _session.Client.CompletionWithTokenAsync(Uri("a.cvl"), 0, 0, cts.Token);
        Assert.True(_backend.WaitUntilEntered(TimeSpan.FromSeconds(5)), "backend completion should be entered");

        cts.Cancel();
        // The server processes the $/cancelRequest notification before the drain
        // request's response, so the in-flight token is cancelled before release.
        await _session.Client.DrainNotificationsAsync();
        _backend.Release();

        try
        {
            CompletionList? result = await inFlight.WithTimeout("cancelled completion");
            Assert.Fail($"Expected cancellation, but completion returned {result?.Items.Length ?? 0} item(s).");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0).WithTimeout("next completion"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextCompletionSucceeds()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _backend.ThrowOnCompletion = true;
        Assert.Null(await _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0).WithTimeout("failing completion"));

        _backend.ThrowOnCompletion = false;
        Assert.NotNull(await _session.Client.CompletionAsync(Uri("a.cvl"), 0, 0).WithTimeout("next completion"));
    }
}
