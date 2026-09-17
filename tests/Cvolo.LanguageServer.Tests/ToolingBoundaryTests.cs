using System.Reflection;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

public class ToolingBoundaryTests
{
    private static readonly string[] ExpectedDirectCompilerReferences = ["Cvolo.Compiler.Tooling"];

    [Fact]
    public void CompilerToolingAssembly_LoadsFromDefaultAlc_AndTypeResolves()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Cvolo.Compiler.Tooling.dll");
        Assert.True(File.Exists(path), $"Expected tooling assembly at {path} (run build/fetch-tooling before tests).");

        var loaded = Assembly.LoadFrom(path);
        Type? workspace = loaded.GetType("Cvolo.Compiler.Tooling.CvoloWorkspace", throwOnError: false);
        Assert.NotNull(workspace);

        var expected = Type.GetType("Cvolo.Compiler.Tooling.CvoloWorkspace, Cvolo.Compiler.Tooling", throwOnError: false);
        Assert.NotNull(expected);
        Assert.Same(expected, workspace);
    }


    [Fact]
    public void DirectCompilerReferences_AreExactlyCompilerToolingOnly()
    {
        Assembly server = typeof(Cvolo.LanguageServer.Protocol.LanguageServer).Assembly;
        string[] direct = server.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("Cvolo.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedDirectCompilerReferences, direct);
    }
}