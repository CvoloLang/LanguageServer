using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic coverage of <c>textDocument/implementation</c>. The request asks which concrete
/// places satisfy the contract at a position, so the backend answer is a plain definition result and
/// the handler maps each target against the text captured with it — an implementation in a closed
/// document still navigates (§6, §7, §8, §44).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class ImplementationHandlerTests : IDisposable
{
    private const string Text = "interface IWidget { int Size(); }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public ImplementationHandlerTests()
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

    private async Task OpenAsync()
    {
        await _session.Client.NotifyDidOpenAsync(Uri, "cvolo", 1, Text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private static BackendDefinitionResult Target(string filePath, string text, int start, int length)
    {
        DocumentUri uri = DocumentUri.Create(filePath);
        return new BackendDefinitionResult(
            new Dictionary<DocumentUri, string> { [uri] = text },
            [new BackendDefinitionTarget(uri, new TextSpan(start, length), new TextSpan(start, length))]);
    }

    private static BackendSymbolInfo Symbol()
    {
        return new BackendSymbolInfo(
            new FakeSymbolHandle(),
            new TextSpan(0, 7),
            "IWidget",
            BackendSymbolKind.Interface,
            "interface IWidget");
    }

    private sealed class FakeSymbolHandle : BackendSymbolHandle
    {
    }

    [Fact]
    public async Task Implementation_ReturnsTheCompilerResolvedSites()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSymbol = Symbol();
        _backend.CannedImplementations = Target(_workspace.PathOf("a.cvl"), Text, 10, 7);

        Location[]? locations = await _session.Client.ImplementationAsync(Uri, 0, 10).WithTimeout("textDocument/implementation");

        Assert.NotNull(locations);
        var location = Assert.Single(locations!);
        Assert.Equal(_workspace.PathOf("a.cvl"), location.Uri.LocalPath);
        Assert.Equal(0, location.Range.Start.Line);
        Assert.Equal(10, location.Range.Start.Character);
        Assert.Equal(17, location.Range.End.Character);
    }

    [Fact]
    public async Task NoImplementationAtThePosition_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedImplementations = null;

        Location[]? locations = await _session.Client.ImplementationAsync(Uri, 0, 10).WithTimeout("textDocument/implementation");

        Assert.Null(locations);
    }

    [Fact]
    public async Task AnImplementationInAClosedDocument_IsMappedAgainstItsCapturedText()
    {
        await StartAsync();
        await OpenAsync();
        const string otherText = "extension Button : IWidget { public int Size() { return 0; } }\n";
        _backend.CannedSymbol = Symbol();
        _backend.CannedImplementations = Target(_workspace.PathOf("b.cvl"), otherText, 10, 6);

        Location[]? locations = await _session.Client.ImplementationAsync(Uri, 0, 10).WithTimeout("textDocument/implementation");

        Assert.NotNull(locations);
        var location = Assert.Single(locations!);
        Assert.Equal(_workspace.PathOf("b.cvl"), location.Uri.LocalPath);
        Assert.Equal(0, location.Range.Start.Line);
        Assert.Equal(10, location.Range.Start.Character);
        Assert.Equal(16, location.Range.End.Character);
    }

    [Fact]
    public async Task TheServerAdvertisesImplementationSupport()
    {
        InitializeResponse response = await _session.Client.InitializeAsync().WithTimeout("initialize");

        Assert.True(response.Capabilities.ImplementationProvider);
    }
}
