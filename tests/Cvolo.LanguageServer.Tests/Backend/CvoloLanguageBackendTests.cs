using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Logging;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

public class CvoloLanguageBackendTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly RecordingCoreLogger _logger;
    private readonly CvoloLanguageBackend _backend;

    public CvoloLanguageBackendTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl", "lib.cvl"]);
        _logger = new RecordingCoreLogger();
        _backend = new CvoloLanguageBackend([_workspace.DirectoryPath], null, _logger);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private DocumentUri MainUri => DocumentUri.Create(_workspace.PathOf("main.cvl"));

    private DocumentUri LibUri => DocumentUri.Create(_workspace.PathOf("lib.cvl"));

    [Fact]
    public void OpenProject_FindsNearestProject_AndResolvesDiscoveredDocuments()
    {
        var project = _backend.OpenProject(MainUri);
        Assert.NotNull(project);

        Assert.True(_backend.TryResolveDocument(project!, MainUri, out var mainHandle));
        Assert.True(_backend.TryResolveDocument(project!, LibUri, out var libHandle));
        Assert.NotSame(mainHandle, libHandle);
    }

    [Fact]
    public void OpenProject_SameDirectory_ReusesSession()
    {
        var first = _backend.OpenProject(MainUri);
        var second = _backend.OpenProject(LibUri);

        Assert.Same(first, second);
    }

    [Fact]
    public void UpdateDocument_AdvancesSnapshot_WithNewText_LeavingOtherDocumentsUntouched()
    {
        var project = _backend.OpenProject(MainUri)!;
        var mainId = ((CvoloDocumentHandle)_backend.Resolve(project, MainUri)).DocumentId;
        var libId = ((CvoloDocumentHandle)_backend.Resolve(project, LibUri)).DocumentId;

        _backend.UpdateDocument(project, _backend.Resolve(project, LibUri), "lib text");

        var snapshot1 = (ToolingBackendSnapshot)_backend.UpdateDocument(project, _backend.Resolve(project, MainUri), "edited main");
        Assert.Equal("edited main", snapshot1.Snapshot.Documents[mainId].Text.ToString());
        Assert.Equal("lib text", snapshot1.Snapshot.Documents[libId].Text.ToString());

        var snapshot2 = (ToolingBackendSnapshot)_backend.UpdateDocument(project, _backend.Resolve(project, MainUri), "edited again");
        Assert.Equal("edited again", snapshot2.Snapshot.Documents[mainId].Text.ToString());
        Assert.Equal("lib text", snapshot2.Snapshot.Documents[libId].Text.ToString());
    }

    [Fact]
    public void RestoreBaseline_RevertsToOnDiskBaseline()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        _backend.UpdateDocument(project, _backend.Resolve(project, MainUri), "overlay text");

        var restored = (ToolingBackendSnapshot)_backend.RestoreBaseline(project, handle);
        var mainId = ((CvoloDocumentHandle)handle).DocumentId;

        Assert.Equal("int Main() { return 0; }", restored.Snapshot.Documents[mainId].Text.ToString());
    }

    [Fact]
    public void DocumentNotInProjectSnapshot_IsNotResolvable()
    {
        var nonexistent = DocumentUri.Create(Path.Combine(_workspace.DirectoryPath, "nonexistent.cvl"));
        var project = _backend.OpenProject(nonexistent);

        Assert.NotNull(project);
        Assert.False(_backend.TryResolveDocument(project!, nonexistent, out _));
        Assert.True(_logger.Has(CoreLogLevel.Error, "not part of project"));
    }

    [Fact]
    public void FileUnderBinDirectory_IsExcludedByToolingDiscovery()
    {
        Directory.CreateDirectory(Path.Combine(_workspace.DirectoryPath, "bin"));
        var hidden = DocumentUri.Create(_workspace.PathOf(Path.Combine("bin", "hidden.cvl")));
        File.WriteAllText(hidden.LocalPath, "int Main() { return 0; }");

        var project = _backend.OpenProject(MainUri)!;
        Assert.False(_backend.TryResolveDocument(project, hidden, out _));
    }

    [Fact]
    public void NoProject_ReturnsNull_AndWarns()
    {
        using var workspace = TestWorkspace.CreateDirectory(["main.cvl"]);
        var noProjectBackend = new CvoloLanguageBackend([], null, _logger);
        var result = noProjectBackend.OpenProject(DocumentUri.Create(Path.Combine(workspace.DirectoryPath, "main.cvl")));

        Assert.Null(result);
        Assert.True(_logger.Has(CoreLogLevel.Warning, "No .cvlproj found"));
    }

    [Fact]
    public void AmbiguousProject_ReturnsNull_AndWarns()
    {
        using var workspace = TestWorkspace.CreateDirectory(["main.cvl"]);
        File.WriteAllText(Path.Combine(workspace.DirectoryPath, "A.cvlproj"), "<Project />");
        File.WriteAllText(Path.Combine(workspace.DirectoryPath, "B.cvlproj"), "<Project />");

        var result = new CvoloLanguageBackend([workspace.DirectoryPath], null, _logger)
            .OpenProject(DocumentUri.Create(Path.Combine(workspace.DirectoryPath, "main.cvl")));

        Assert.Null(result);
        Assert.True(_logger.Has(CoreLogLevel.Warning, "Ambiguous project"));
    }

    [Fact]
    public void NoWorkspaceRoot_UsesFilesystemAsBoundary()
    {
        using var workspace = TestWorkspace.CreateProject(["deep/main.cvl"]);
        var unbounded = new CvoloLanguageBackend([], null, _logger);
        var project = unbounded.OpenProject(DocumentUri.Create(Path.Combine(workspace.DirectoryPath, "deep", "main.cvl")));

        Assert.NotNull(project);
    }

    [Fact]
    public void MostSpecificContainingWorkspaceFolder_IsTheBoundary()
    {
        using var workspace = TestWorkspace.CreateDirectory(["App.cvlproj", "nested/main.cvl"]);
        var nested = Path.Combine(workspace.DirectoryPath, "nested");
        var multiRoot = new CvoloLanguageBackend([workspace.DirectoryPath, nested], null, _logger);

        var result = multiRoot.OpenProject(DocumentUri.Create(Path.Combine(nested, "main.cvl")));

        Assert.Null(result);
        Assert.True(_logger.Has(CoreLogLevel.Warning, "No .cvlproj found"));
    }

    [Fact]
    public void MultiRoot_TwoFolders_EachDocumentUsesItsOwnBoundary()
    {
        using var parent = TestWorkspace.CreateDirectory([]);
        var rootA = Path.Combine(parent.DirectoryPath, "A");
        var rootB = Path.Combine(parent.DirectoryPath, "B");
        WriteProject(rootA, "a.cvl");
        WriteProject(rootB, "b.cvl");

        var multiRoot = new CvoloLanguageBackend([rootA, rootB], null, _logger);
        var projectA = multiRoot.OpenProject(DocumentUri.Create(Path.Combine(rootA, "a.cvl")));
        var projectB = multiRoot.OpenProject(DocumentUri.Create(Path.Combine(rootB, "b.cvl")));

        Assert.NotNull(projectA);
        Assert.NotNull(projectB);
        Assert.NotSame(projectA, projectB);
        Assert.True(multiRoot.TryResolveDocument(projectA!, DocumentUri.Create(Path.Combine(rootA, "a.cvl")), out _));
        Assert.True(multiRoot.TryResolveDocument(projectB!, DocumentUri.Create(Path.Combine(rootB, "b.cvl")), out _));
    }

    [Fact]
    public void DocumentOutsideAllWorkspaceFolders_HasNoBoundary()
    {
        var elsewhere = Path.Combine(_workspace.DirectoryPath, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var boundedElsewhere = new CvoloLanguageBackend([elsewhere], null, _logger);

        var project = boundedElsewhere.OpenProject(MainUri);

        Assert.NotNull(project);
        var session = (CvoloProjectSession)project!;
        Assert.Equal(_workspace.DirectoryPath, session.Project.ProjectPath);
    }

    [Fact]
    public async Task ConcurrentUpdates_TwoDocuments_SameProject_NoLostUpdates()
    {
        var project = _backend.OpenProject(MainUri)!;
        var mainHandle = _backend.Resolve(project, MainUri);
        var libHandle = _backend.Resolve(project, LibUri);
        var mainId = ((CvoloDocumentHandle)mainHandle).DocumentId;
        var libId = ((CvoloDocumentHandle)libHandle).DocumentId;

        const int iterations = 250;
        var mainWrites = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                _backend.UpdateDocument(project, mainHandle, $"main-{i}");
            }
        });
        var libWrites = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                _backend.UpdateDocument(project, libHandle, $"lib-{i}");
            }
        });

        await Task.WhenAll(mainWrites, libWrites);

        var snapshot = ((CvoloProjectSession)project).Current;
        Assert.Equal($"main-{iterations - 1}", snapshot.Documents[mainId].Text.ToString());
        Assert.Equal($"lib-{iterations - 1}", snapshot.Documents[libId].Text.ToString());
    }

    [Fact]
    public void DocumentOutsideAllFolders_InsideFallbackRoot_IsBoundedByFallbackRoot()
    {
        using var parent = TestWorkspace.CreateDirectory(["Inner/sub/doc.cvl"]);
        File.WriteAllText(Path.Combine(parent.DirectoryPath, "App.cvlproj"), "<Project><ItemGroup /></Project>\r\n");
        var folderA = Path.Combine(parent.DirectoryPath, "FolderA");
        Directory.CreateDirectory(folderA);
        var fallbackRoot = Path.Combine(parent.DirectoryPath, "Inner");

        var bounded = new CvoloLanguageBackend([folderA], fallbackRoot, _logger);
        var result = bounded.OpenProject(DocumentUri.Create(Path.Combine(fallbackRoot, "sub", "doc.cvl")));

        Assert.Null(result);
        Assert.True(_logger.Has(CoreLogLevel.Warning, "No .cvlproj found"));
    }

    [Fact]
    public void NoFoldersAndNoFallbackRoot_IsUnbounded()
    {
        using var parent = TestWorkspace.CreateDirectory(["Inner/sub/doc.cvl"]);
        File.WriteAllText(Path.Combine(parent.DirectoryPath, "App.cvlproj"), "<Project><ItemGroup /></Project>\r\n");
        var unbounded = new CvoloLanguageBackend([], null, _logger);
        var document = DocumentUri.Create(Path.Combine(parent.DirectoryPath, "Inner", "sub", "doc.cvl"));

        var project = unbounded.OpenProject(document);

        Assert.NotNull(project);
        var session = (CvoloProjectSession)project!;
        Assert.Equal(parent.DirectoryPath, session.Project.ProjectPath);
    }

    private static void WriteProject(string directory, string documentRelativePath)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "App.cvlproj"), "<Project><ItemGroup /></Project>\r\n");
        File.WriteAllText(Path.Combine(directory, documentRelativePath), "int Main() { return 0; }");
    }
}

internal static class BackendExtensions
{
    public static BackendDocumentHandle Resolve(this ILanguageBackend backend, BackendProject project, DocumentUri uri)
    {
        Assert.True(backend.TryResolveDocument(project, uri, out var handle));
        return handle;
    }
}