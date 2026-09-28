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
/// Deterministic coverage of <c>textDocument/typeDefinition</c>. The request asks what semantic type
/// the value or expression at a position has, so the backend answer is a plain definition result and
/// the handler maps each target against the text captured with it — a type declared in a closed
/// document still navigates (§2, §4, §5, §42).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class TypeDefinitionHandlerTests : IDisposable
{
    private const string Text = "struct Point { int x; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public TypeDefinitionHandlerTests()
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

    [Fact]
    public async Task TypeDefinition_ReturnsTheCompilerResolvedTypeDeclaration()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeDefinitions = Target(_workspace.PathOf("a.cvl"), Text, 7, 5);

        Location[]? locations = await _session.Client.TypeDefinitionAsync(Uri, 0, 7).WithTimeout("textDocument/typeDefinition");

        Assert.NotNull(locations);
        var location = Assert.Single(locations!);
        Assert.Equal(_workspace.PathOf("a.cvl"), location.Uri.LocalPath);
        Assert.Equal(0, location.Range.Start.Line);
        Assert.Equal(7, location.Range.Start.Character);
        Assert.Equal(12, location.Range.End.Character);
    }

    [Fact]
    public async Task NoResolvedTypeAtThePosition_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeDefinitions = null;

        Location[]? locations = await _session.Client.TypeDefinitionAsync(Uri, 0, 7).WithTimeout("textDocument/typeDefinition");

        Assert.Null(locations);
    }

    [Fact]
    public async Task ATargetInAClosedDocument_IsMappedAgainstItsCapturedText()
    {
        await StartAsync();
        await OpenAsync();
        const string otherText = "public struct Other { public int y; }\n";
        _backend.CannedTypeDefinitions = Target(_workspace.PathOf("b.cvl"), otherText, 14, 5);

        Location[]? locations = await _session.Client.TypeDefinitionAsync(Uri, 0, 7).WithTimeout("textDocument/typeDefinition");

        Assert.NotNull(locations);
        var location = Assert.Single(locations!);
        Assert.Equal(_workspace.PathOf("b.cvl"), location.Uri.LocalPath);
        Assert.Equal(0, location.Range.Start.Line);
        Assert.Equal(14, location.Range.Start.Character);
        Assert.Equal(19, location.Range.End.Character);
    }

    [Fact]
    public async Task TheServerAdvertisesTypeDefinitionSupport()
    {
        InitializeResponse response = await _session.Client.InitializeAsync().WithTimeout("initialize");

        Assert.True(response.Capabilities.TypeDefinitionProvider);
    }
}
