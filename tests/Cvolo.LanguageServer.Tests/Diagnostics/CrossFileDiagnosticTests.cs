using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class CrossFileDiagnosticTests : IDisposable
{
    private const string BothDefineMain = "int Main() { return 0; }";

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public CrossFileDiagnosticTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl", "lib.cvl"], _ => BothDefineMain);
        _session = ProtocolSession.Start(new RecordingLspLogger());
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Uri MainUri => _workspace.DocumentUri("main.cvl");

    private Uri LibUri => _workspace.DocumentUri("lib.cvl");

    [Fact]
    public async Task EditInOneDocument_ClearsDiagnosticInAnother()
    {
        await _session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath).WithTimeout("initialize");

        await _session.Client.NotifyDidOpenAsync(MainUri, "cvolo", 1, BothDefineMain).WithTimeout("didOpen main");
        await WaitForAsync(MainUri, published => published.Count > 0, "duplicate diagnostic in main");

        await _session.Client.NotifyDidOpenAsync(LibUri, "cvolo", 1, BothDefineMain).WithTimeout("didOpen lib");
        await _session.Client.WaitUntilAsync(() => _session.Server.Store.OpenUris.Count == 2, "both documents open");

        await _session.Client.NotifyDidChangeAsync(LibUri, 2, new TextDocumentContentChangeEvent { Text = "int Lib() { return 0; }" }).WithTimeout("didChange lib");

        await WaitForAsync(MainUri, published => published.Last().Count == 0, "main cleared after lib edit");
    }

    private Task WaitForAsync(Uri uri, Func<IReadOnlyList<PublishedDiagnostics>, bool> condition, string label)
    {
        return _session.Client.WaitUntilAsync(
            () => condition(DiagnosticFrames.ForUri(_session.Server, uri)),
            label,
            TimeSpan.FromSeconds(10));
    }
}
