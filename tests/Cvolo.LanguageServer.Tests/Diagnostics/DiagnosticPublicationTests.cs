using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class DiagnosticPublicationTests : IDisposable
{
    private const string InvalidText = "int Main( { return 0; }";
    private const string ValidText = "int Main() { return 0; }";

    private readonly TestWorkspace _workspace;
    private readonly RecordingLspLogger _logger;
    private readonly ProtocolSession _session;

    public DiagnosticPublicationTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"]);
        _logger = new RecordingLspLogger();
        _session = ProtocolSession.Start(_logger);
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Uri MainUri => _workspace.DocumentUri("main.cvl");

    [Fact]
    public async Task DidOpen_InvalidDocument_PublishesParserDiagnostic()
    {
        await InitializeAsync();
        await _session.Client.NotifyDidOpenAsync(MainUri, "cvolo", 1, InvalidText).WithTimeout("didOpen");
        await WaitForAsync(published => published.Count > 0, "diagnostics published");

        PublishedDiagnostics latest = DiagnosticFrames.ForUri(_session.Server, MainUri).Last();
        var diagnostic = (JObject)latest.Diagnostics[0];
        Assert.Equal(1, diagnostic["severity"]!.Value<int>());
        Assert.Equal("cvolo", diagnostic["source"]!.Value<string>());
        Assert.Equal("CVL0000", diagnostic["code"]!.Value<string>());
    }

    [Fact]
    public async Task DidChange_ToValidText_RefreshesDiagnosticsToEmpty()
    {
        await InitializeAsync();
        await _session.Client.NotifyDidOpenAsync(MainUri, "cvolo", 1, InvalidText).WithTimeout("didOpen");
        await WaitForAsync(published => published.Count > 0, "diagnostics published");

        await _session.Client.NotifyDidChangeAsync(MainUri, 2, new TextDocumentContentChangeEvent { Text = ValidText }).WithTimeout("didChange");
        await WaitForAsync(published => published.Count >= 2 && published.Last().Count == 0, "empty refresh published");
    }

    [Fact]
    public async Task DidClose_PublishesEmptyForClosedDocument()
    {
        await InitializeAsync();
        await _session.Client.NotifyDidOpenAsync(MainUri, "cvolo", 1, InvalidText).WithTimeout("didOpen");
        await WaitForAsync(published => published.Count > 0, "diagnostics published");

        await _session.Client.NotifyDidCloseAsync(MainUri).WithTimeout("didClose");
        await WaitForAsync(published => published.Last().Count == 0, "empty on close");
    }

    [Fact]
    public async Task CloseThenReopen_DoesNotRepublishOldSessionDiagnostics()
    {
        await InitializeAsync();
        await _session.Client.NotifyDidOpenAsync(MainUri, "cvolo", 1, InvalidText).WithTimeout("didOpen");
        await WaitForAsync(published => published.Count > 0, "diagnostics published");

        await _session.Client.NotifyDidCloseAsync(MainUri).WithTimeout("didClose");
        await WaitForAsync(published => published.Last().Count == 0, "empty on close");

        await _session.Client.NotifyDidOpenAsync(MainUri, "cvolo", 1, ValidText).WithTimeout("reopen");
        await WaitForAsync(published => published.Count >= 3, "reopen publish");
        await Task.Delay(300);

        Assert.Equal(0, DiagnosticFrames.ForUri(_session.Server, MainUri).Last().Count);
    }

    private Task InitializeAsync()
    {
        return _session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath).WithTimeout("initialize");
    }

    private Task WaitForAsync(Func<IReadOnlyList<PublishedDiagnostics>, bool> condition, string label)
    {
        return _session.Client.WaitUntilAsync(
            () => condition(DiagnosticFrames.ForUri(_session.Server, MainUri)),
            label,
            TimeSpan.FromSeconds(10));
    }
}
