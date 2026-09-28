using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic coverage of the declared contract hierarchy. Prepare names the contract at a
/// position; the two follow-up requests carry the server-owned key rather than a snapshot-scoped
/// symbol, so an intervening edit re-resolves the contract against the current snapshot (§12-§15,
/// §38, §42).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class TypeHierarchyHandlerTests : IDisposable
{
    private const string Text = "interface IWidget { int Size(); }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public TypeHierarchyHandlerTests()
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

    private static BackendTypeHierarchyResult Hierarchy(
        string filePath,
        string text,
        int start,
        int length,
        string name,
        string key,
        BackendSymbolKind kind)
    {
        DocumentUri uri = DocumentUri.Create(filePath);
        return new BackendTypeHierarchyResult(
            new Dictionary<DocumentUri, string> { [uri] = text },
            [new BackendHierarchyItem(name, kind, uri, new TextSpan(start, length), new TextSpan(start, length), key)]);
    }

    private TypeHierarchyItemPayload ItemFor(string filePath, string key)
    {
        return new TypeHierarchyItemPayload
        {
            Name = key,
            Kind = SymbolKind.Interface,
            Uri = new Uri(filePath).AbsoluteUri,
            Data = key,
        };
    }

    [Fact]
    public async Task Prepare_ReturnsTheContractDeclaredAtThePosition()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedPreparedHierarchy = Hierarchy(
            _workspace.PathOf("a.cvl"), Text, 10, 7, "IWidget", "IWidget", BackendSymbolKind.Interface);

        JArray? items = await _session.Client.PrepareTypeHierarchyAsync(Uri, 0, 10).WithTimeout("textDocument/prepareTypeHierarchy");

        Assert.NotNull(items);
        var item = Assert.Single(items!.Children());
        Assert.Equal("IWidget", item["name"]!.Value<string>());
        Assert.Equal("IWidget", item["data"]!.Value<string>());
        Assert.Equal((int)SymbolKind.Interface, item["kind"]!.Value<int>());
        Assert.Equal(10, item["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(17, item["range"]!["end"]!["character"]!.Value<int>());
    }

    [Fact]
    public async Task APositionThatNamesNoContract_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedPreparedHierarchy = null;

        JArray? items = await _session.Client.PrepareTypeHierarchyAsync(Uri, 0, 10).WithTimeout("textDocument/prepareTypeHierarchy");

        Assert.Null(items);
    }

    [Fact]
    public async Task Supertypes_ReResolveTheContractByKey()
    {
        await StartAsync();
        await OpenAsync();
        const string baseText = "interface IWidget { int Size(); }\n";
        _backend.CannedSupertypes = Hierarchy(
            _workspace.PathOf("b.cvl"), baseText, 10, 7, "IWidget", "IWidget", BackendSymbolKind.Interface);

        JArray? items = await _session.Client
            .TypeHierarchySupertypesAsync(ItemFor(_workspace.PathOf("a.cvl"), "IWidget"))
            .WithTimeout("typeHierarchy/supertypes");

        Assert.NotNull(items);
        var item = Assert.Single(items!.Children());
        Assert.Equal("IWidget", item["name"]!.Value<string>());
        Assert.Equal(_workspace.PathOf("b.cvl"), new Uri(item["uri"]!.Value<string>()!).LocalPath);
        Assert.Equal("IWidget", _backend.LastHierarchyKey);
    }

    [Fact]
    public async Task Subtypes_ReturnTheDeclaredChildren()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSubtypes = Hierarchy(
            _workspace.PathOf("a.cvl"), Text, 0, 7, "IButton", "IButton", BackendSymbolKind.Interface);

        JArray? items = await _session.Client
            .TypeHierarchySubtypesAsync(ItemFor(_workspace.PathOf("a.cvl"), "IWidget"))
            .WithTimeout("typeHierarchy/subtypes");

        Assert.NotNull(items);
        var item = Assert.Single(items!.Children());
        Assert.Equal("IButton", item["name"]!.Value<string>());
        Assert.Equal("IWidget", _backend.LastHierarchyKey);
    }

    [Fact]
    public async Task ARelatedContractInAClosedDocument_IsMappedAgainstItsCapturedText()
    {
        await StartAsync();
        await OpenAsync();
        const string otherText = "interface IBase { int Key(); }\n";
        _backend.CannedSupertypes = Hierarchy(
            _workspace.PathOf("b.cvl"), otherText, 10, 5, "IBase", "IBase", BackendSymbolKind.Interface);

        JArray? items = await _session.Client
            .TypeHierarchySupertypesAsync(ItemFor(_workspace.PathOf("a.cvl"), "IWidget"))
            .WithTimeout("typeHierarchy/supertypes");

        Assert.NotNull(items);
        var item = Assert.Single(items!.Children());
        Assert.Equal(_workspace.PathOf("b.cvl"), new Uri(item["uri"]!.Value<string>()!).LocalPath);
        Assert.Equal(10, item["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(15, item["range"]!["end"]!["character"]!.Value<int>());
    }

    [Fact]
    public async Task TheServerAdvertisesTypeHierarchySupport()
    {
        InitializeResponse response = await _session.Client.InitializeAsync().WithTimeout("initialize");

        Assert.True(response.Capabilities.TypeHierarchyProvider);
    }
}
