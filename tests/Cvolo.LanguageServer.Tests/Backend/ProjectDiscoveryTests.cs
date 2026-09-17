using Cvolo.LanguageServer.Cvolo;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

public class ProjectDiscoveryTests : IDisposable
{
    private readonly string _root;

    public ProjectDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cvolo-discovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void NearestSingleProject_Wins_WhenDocumentIsInProjectDirectory()
    {
        Write("App.cvlproj", "");
        var result = ProjectDiscovery.FindProject(Path.Combine(_root, "main.cvl"), _root);

        Assert.Equal(ProjectDiscoveryStatus.Found, result.Status);
        Assert.Equal(_root, result.ProjectDirectory);
    }

    [Fact]
    public void WalkUp_FindsProjectInAncestorDirectory()
    {
        Write("App.cvlproj", "");
        var nested = Path.Combine(_root, "src", "deep");
        Directory.CreateDirectory(nested);
        var result = ProjectDiscovery.FindProject(Path.Combine(nested, "main.cvl"), _root);

        Assert.Equal(ProjectDiscoveryStatus.Found, result.Status);
        Assert.Equal(_root, result.ProjectDirectory);
    }

    [Fact]
    public void MultipleProjectsInSameDirectory_IsAmbiguous()
    {
        Write("A.cvlproj", "");
        Write("B.cvlproj", "");
        var result = ProjectDiscovery.FindProject(Path.Combine(_root, "main.cvl"), _root);

        Assert.Equal(ProjectDiscoveryStatus.AmbiguousProject, result.Status);
        Assert.Equal(_root, result.ProjectDirectory);
    }

    [Fact]
    public void NoProjectAnywhere_IsNoProject()
    {
        var result = ProjectDiscovery.FindProject(Path.Combine(_root, "main.cvl"), _root);

        Assert.Equal(ProjectDiscoveryStatus.NoProject, result.Status);
    }

    [Fact]
    public void WalkDoesNotCrossWorkspaceBoundary()
    {
        Write("App.cvlproj", "");
        var nested = Path.Combine(_root, "src", "deep");
        Directory.CreateDirectory(nested);
        var result = ProjectDiscovery.FindProject(Path.Combine(nested, "main.cvl"), Path.Combine(_root, "src"));

        Assert.Equal(ProjectDiscoveryStatus.NoProject, result.Status);
    }

    [Fact]
    public void ProjectAtWorkspaceBoundary_IsFound()
    {
        var boundary = Path.Combine(_root, "src");
        Directory.CreateDirectory(boundary);
        Write(Path.Combine("src", "App.cvlproj"), "");
        var nested = Path.Combine(boundary, "deep");
        Directory.CreateDirectory(nested);
        var result = ProjectDiscovery.FindProject(Path.Combine(nested, "main.cvl"), boundary);

        Assert.Equal(ProjectDiscoveryStatus.Found, result.Status);
        Assert.Equal(boundary, result.ProjectDirectory);
    }

    [Fact]
    public void RelativeDocumentPath_IsNoProject()
    {
        var result = ProjectDiscovery.FindProject("main.cvl", _root);
        Assert.Equal(ProjectDiscoveryStatus.NoProject, result.Status);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}