using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class TextDocumentSyncHandlerTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly RecordingLspLogger _logger;
    private readonly ProtocolSession _session;

    public TextDocumentSyncHandlerTests()
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

    private async Task<DocumentStore> StartSessionAsync()
    {
        await _session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath).WithTimeout("initialize");
        return _session.Server.Store;
    }

    [Fact]
    public async Task DidOpen_OpensDocument_WithEditorText()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));

        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "editor text").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "editor text", "didOpen applied");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("editor text", state!.Text);
        Assert.Equal(1, state.Version.Value);
        Assert.Equal("cvolo", state.LanguageId);
    }

    [Fact]
    public async Task DidOpen_Duplicate_KeepsOriginalTextAndVersion()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));

        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "AAA").WithTimeout("first didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "AAA", "first didOpen applied");

        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 2, "BBB").WithTimeout("second didOpen");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "Duplicate didOpen"), "duplicate warning");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("AAA", state!.Text);
        Assert.Equal(1, state.Version.Value);
    }

    [Fact]
    public async Task DidChange_IncrementalReplace_UpdatesText()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "hello world").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "hello world", "didOpen applied");

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 2, Change("cvolo", 6, 11)).WithTimeout("didChange");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "hello cvolo", "didChange applied");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("hello cvolo", state!.Text);
        Assert.Equal(2, state.Version.Value);
    }

    [Fact]
    public async Task DidChange_InvalidRange_IsTransactional()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "abc\n").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "abc\n", "didOpen applied");

        var bad = new TextDocumentContentChangeEvent
        {
            Range = new LspRange { Start = new Position { Line = 7, Character = 0 }, End = new Position { Line = 7, Character = 0 } },
            Text = "X",
        };
        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 2, Change("Q", 0, 0), bad).WithTimeout("invalid didChange");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "nothing applied"), "invalid-range warning");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("abc\n", state!.Text);
        Assert.Equal(1, state.Version.Value);

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 2, Change("Q", 0, 0)).WithTimeout("retry didChange");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "Qabc\n", "retry didChange applied");

        Assert.True(store.TryGet(coreUri, out var updated));
        Assert.Equal("Qabc\n", updated!.Text);
        Assert.Equal(2, updated.Version.Value);
    }

    [Fact]
    public async Task DidChange_StaleVersion_IsIgnored()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "original").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "original", "didOpen applied");

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 1, Change("X", 0, 0)).WithTimeout("stale didChange");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "Stale didChange"), "stale warning");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("original", state!.Text);
        Assert.Equal(1, state.Version.Value);
    }

    [Fact]
    public async Task DidChange_ForUnopenedDocument_DoesNotImplicitlyOpen()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 1, Change("X", 0, 0)).WithTimeout("didChange");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "didChange for unopened"), "unopened warning");

        Assert.Empty(store.OpenUris);
    }

    [Fact]
    public async Task DidChange_FullReplacement_WithNoRange()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "old").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "old", "didOpen applied");

        await _session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 2, new TextDocumentContentChangeEvent { Text = "brand new" }).WithTimeout("didChange");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "brand new", "full replacement applied");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("brand new", state!.Text);
        Assert.Equal(2, state.Version.Value);
    }

    [Fact]
    public async Task DidClose_RemovesState_AndCloseUnopenedIsTolerated()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "aaa").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out _), "didOpen applied");

        await _session.Client.NotifyDidCloseAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("didClose");
        await _session.Client.WaitUntilAsync(() => !store.TryGet(coreUri, out _), "didClose applied");
        Assert.False(store.TryGet(coreUri, out _));

        await _session.Client.NotifyDidCloseAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("second didClose");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "didClose for unopened"), "second didClose warning");
    }

    [Fact]
    public async Task ReopenAfterClose_GetsNewSession()
    {
        var store = await StartSessionAsync();
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "one").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "one", "didOpen applied");

        Assert.True(store.TryGet(coreUri, out var first));

        await _session.Client.NotifyDidCloseAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("didClose");
        await _session.Client.WaitUntilAsync(() => !store.TryGet(coreUri, out _), "didClose applied");

        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "two").WithTimeout("reopen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "two", "reopen applied");

        Assert.True(store.TryGet(coreUri, out var second));
        Assert.NotEqual(first!.SessionId, second!.SessionId);
    }

    private static TextDocumentContentChangeEvent Change(string text, int startChar, int endChar)
    {
        return new TextDocumentContentChangeEvent
        {
            Range = new LspRange
            {
                Start = new Position { Line = 0, Character = startChar },
                End = new Position { Line = 0, Character = endChar },
            },
            Text = text,
        };
    }

    [Fact]
    public async Task WorkspaceFolders_TwoRoots_OpensDocumentsInBoth()
    {
        using var parent = TestWorkspace.CreateDirectory([]);
        var rootA = Path.Combine(parent.DirectoryPath, "A");
        var rootB = Path.Combine(parent.DirectoryPath, "B");
        WriteProject(rootA, "a.cvl");
        WriteProject(rootB, "b.cvl");

        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            WorkspaceFolders = [Folder(rootA, "A"), Folder(rootB, "B")],
        }).WithTimeout("initialize");
        var store = _session.Server.Store;

        var uriA = new Uri(Path.Combine(rootA, "a.cvl"));
        var uriB = new Uri(Path.Combine(rootB, "b.cvl"));
        var coreA = DocumentUri.Create(Path.Combine(rootA, "a.cvl"));
        var coreB = DocumentUri.Create(Path.Combine(rootB, "b.cvl"));

        await _session.Client.NotifyDidOpenAsync(uriA, "cvolo", 1, "text a").WithTimeout("didOpen a");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreA, out var s) && s.Text == "text a", "didOpen a applied");
        await _session.Client.NotifyDidOpenAsync(uriB, "cvolo", 1, "text b").WithTimeout("didOpen b");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreB, out var s) && s.Text == "text b", "didOpen b applied");

        Assert.True(store.TryGet(coreA, out var stateA));
        Assert.Equal("text a", stateA!.Text);
        Assert.True(store.TryGet(coreB, out var stateB));
        Assert.Equal("text b", stateB!.Text);
    }

    [Fact]
    public async Task WorkspaceFolders_MostSpecificNestedFolder_IsNotCrossed()
    {
        using var workspace = TestWorkspace.CreateDirectory(["App.cvlproj", "nested/deeper/x.cvl"]);
        var nested = Path.Combine(workspace.DirectoryPath, "nested");
        var documentPath = Path.Combine(nested, "deeper", "x.cvl");

        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            WorkspaceFolders = [Folder(workspace.DirectoryPath, "root"), Folder(nested, "nested")],
        }).WithTimeout("initialize");
        var store = _session.Server.Store;

        await _session.Client.NotifyDidOpenAsync(new Uri(documentPath), "cvolo", 1, "t").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "No .cvlproj found"), "no-project warning");

        Assert.Empty(store.OpenUris);
    }

    [Fact]
    public async Task NoWorkspaceFolders_FallsBackToRootPath_Boundary()
    {
        using var workspace = TestWorkspace.CreateProject(["main.cvl"]);

        await _session.Client.InitializeWithAsync(new InitializeRequestParams { RootPath = workspace.DirectoryPath }).WithTimeout("initialize");
        var store = _session.Server.Store;
        var coreUri = DocumentUri.Create(workspace.PathOf("main.cvl"));

        await _session.Client.NotifyDidOpenAsync(workspace.DocumentUri("main.cvl"), "cvolo", 1, "root path text").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "root path text", "didOpen applied");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("root path text", state!.Text);
    }

    [Fact]
    public async Task DocumentOutsideAllWorkspaceFolders_InsideRootUri_FallsBackToRootUri()
    {
        using var parent = TestWorkspace.CreateDirectory([]);
        var rootUri = Path.Combine(parent.DirectoryPath, "Inner");
        var folderA = Path.Combine(parent.DirectoryPath, "FolderA");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(Path.Combine(rootUri, "sub"));
        File.WriteAllText(Path.Combine(parent.DirectoryPath, "App.cvlproj"), "<Project><ItemGroup /></Project>\r\n");
        File.WriteAllText(Path.Combine(rootUri, "sub", "doc.cvl"), "int Main() { return 0; }");
        var documentPath = Path.Combine(rootUri, "sub", "doc.cvl");

        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            WorkspaceFolders = [Folder(folderA, "folderA")],
            RootUri = new Uri(rootUri),
        }).WithTimeout("initialize");
        var store = _session.Server.Store;

        await _session.Client.NotifyDidOpenAsync(new Uri(documentPath), "cvolo", 1, "t").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "No .cvlproj found"), "no-project warning");

        Assert.Empty(store.OpenUris);
    }

    [Fact]
    public async Task DocumentOutsideAllWorkspaceFolders_FallsBackToRootPath_WhenRootUriAbsent()
    {
        using var parent = TestWorkspace.CreateDirectory([]);
        var rootPath = Path.Combine(parent.DirectoryPath, "Inner");
        var folderA = Path.Combine(parent.DirectoryPath, "FolderA");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(Path.Combine(rootPath, "sub"));
        File.WriteAllText(Path.Combine(parent.DirectoryPath, "App.cvlproj"), "<Project><ItemGroup /></Project>\r\n");
        File.WriteAllText(Path.Combine(rootPath, "sub", "doc.cvl"), "int Main() { return 0; }");
        var documentPath = Path.Combine(rootPath, "sub", "doc.cvl");

        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            WorkspaceFolders = [Folder(folderA, "folderA")],
            RootPath = rootPath,
        }).WithTimeout("initialize");
        var store = _session.Server.Store;

        await _session.Client.NotifyDidOpenAsync(new Uri(documentPath), "cvolo", 1, "t").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => _logger.Has("Warning", "No .cvlproj found"), "no-project warning");

        Assert.Empty(store.OpenUris);
    }

    [Fact]
    public async Task NoRootAndNoFolders_HasUnboundedDiscovery()
    {
        using var parent = TestWorkspace.CreateDirectory([]);
        var inner = Path.Combine(parent.DirectoryPath, "Inner");
        Directory.CreateDirectory(Path.Combine(inner, "sub"));
        File.WriteAllText(Path.Combine(parent.DirectoryPath, "App.cvlproj"), "<Project><ItemGroup /></Project>\r\n");
        File.WriteAllText(Path.Combine(inner, "sub", "doc.cvl"), "int Main() { return 0; }");
        var documentPath = Path.Combine(inner, "sub", "doc.cvl");

        await _session.Client.InitializeWithAsync(new InitializeRequestParams()).WithTimeout("initialize");
        var store = _session.Server.Store;
        var coreUri = DocumentUri.Create(documentPath);

        await _session.Client.NotifyDidOpenAsync(new Uri(documentPath), "cvolo", 1, "unbounded text").WithTimeout("didOpen");
        await _session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out var s) && s.Text == "unbounded text", "didOpen applied");

        Assert.True(store.TryGet(coreUri, out var state));
        Assert.Equal("unbounded text", state!.Text);
    }

    private static WorkspaceFolderItem Folder(string directoryPath, string name)
    {
        return new WorkspaceFolderItem(new Uri(directoryPath).ToString(), name);
    }

    private static void WriteProject(string directory, string documentRelativePath)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "App.cvlproj"), "<Project><ItemGroup /></Project>\r\n");
        File.WriteAllText(Path.Combine(directory, documentRelativePath), "int Main() { return 0; }");
    }
}