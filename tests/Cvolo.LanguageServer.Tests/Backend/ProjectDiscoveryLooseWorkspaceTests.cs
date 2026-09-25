using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Tests.Backend;

public sealed class ProjectDiscoveryLooseWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-lsp-loose", Guid.NewGuid().ToString("N"));

    public ProjectDiscoveryLooseWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void NoProjectAtWorkspaceRoot_ReturnsLooseWorkspace()
    {
        var source = Path.Combine(_root, "Main.cvl");
        File.WriteAllText(source, "int main() { return 0; }\n");

        ProjectDiscoveryResult result = ProjectDiscovery.FindProject(source, _root);

        Assert.Equal(ProjectDiscoveryStatus.LooseWorkspace, result.Status);
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(result.ProjectDirectory!));
    }

    [Fact]
    public void ExistingProjectStillWinsOverLooseWorkspace()
    {
        var source = Path.Combine(_root, "Main.cvl");
        File.WriteAllText(source, "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(_root, "App.cvlproj"), "<Project Sdk=\"Cvolo.Sdk\" />");

        ProjectDiscoveryResult result = ProjectDiscovery.FindProject(source, _root);

        Assert.Equal(ProjectDiscoveryStatus.Found, result.Status);
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(result.ProjectDirectory!));
    }

    [Fact]
    public void NestedProjectOutsideLooseFile_DoesNotGetMergedIntoLooseRoot()
    {
        var looseDirectory = Path.Combine(_root, "scratch");
        var nestedProject = Path.Combine(_root, "app");
        Directory.CreateDirectory(looseDirectory);
        Directory.CreateDirectory(nestedProject);
        var source = Path.Combine(looseDirectory, "Scratch.cvl");
        File.WriteAllText(source, "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(nestedProject, "App.cvlproj"), "<Project Sdk=\"Cvolo.Sdk\" />");

        ProjectDiscoveryResult result = ProjectDiscovery.FindProject(source, _root);

        Assert.Equal(ProjectDiscoveryStatus.LooseWorkspace, result.Status);
        Assert.Equal(Path.GetFullPath(looseDirectory), Path.GetFullPath(result.ProjectDirectory!));
    }

    [Fact]
    public void RealProject_IgnoresLooseWorkspaceLibraryPaths()
    {
        var source = Path.Combine(_root, "Main.cvl");
        File.WriteAllText(source, "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(_root, "App.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
</Project>
""");
        var missingLooseLibrary = Path.Combine(_root, "does-not-exist.cvlib");
        var backend = new CvoloLanguageBackend([_root], _root, libraryPaths: [missingLooseLibrary]);

        BackendProject? project = backend.OpenProject(DocumentUri.Create(source));

        Assert.NotNull(project);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
