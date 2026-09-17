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
    public void Server_ReferencesExactlyCompilerTooling_AmongCompilerFamily()
    {
        Assembly server = typeof(global::Cvolo.LanguageServer.Protocol.LanguageServer).Assembly;
        string[] direct = DirectCompilerFamilyReferences(server);

        Assert.Equal(ExpectedDirectCompilerReferences, direct);
    }

    [Fact]
    public void Core_ReferencesNoCompilerProtocolOrAntlrAssemblies()
    {
        Assembly core = typeof(global::Cvolo.LanguageServer.Core.DocumentStore).Assembly;
        string[] names = DirectReferenceNames(core);

        Assert.DoesNotContain(names, n => n.StartsWith("Cvolo.", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Antlr4.Runtime", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
    }

    [Fact]
    public void Cvolo_ReferencesExactlyCompilerTooling_AmongCompilerFamily()
    {
        Assembly cvolo = typeof(global::Cvolo.LanguageServer.Cvolo.CvoloLanguageBackend).Assembly;
        string[] direct = DirectCompilerFamilyReferences(cvolo);

        Assert.Equal(ExpectedDirectCompilerReferences, direct);
        Assert.DoesNotContain(DirectReferenceNames(cvolo), n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
    }

    private static string[] DirectCompilerFamilyReferences(Assembly assembly)
    {
        return DirectReferenceNames(assembly)
            .Where(name => name.StartsWith("Cvolo.", StringComparison.Ordinal) && !name.StartsWith("Cvolo.LanguageServer.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] DirectReferenceNames(Assembly assembly)
    {
        return assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
