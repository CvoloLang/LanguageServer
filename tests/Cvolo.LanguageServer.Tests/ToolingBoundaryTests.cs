using System.Reflection;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

public class ToolingBoundaryTests
{
    private static readonly string[] ExpectedDirectCompilerReferences = ["Cvolo.Compiler.Tooling"];

    private static bool ManagedToolingAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "Cvolo.Compiler.Tooling.dll"));

    [Fact]
    public void CompilerToolingAssembly_LoadsFromDefaultAlc_AndTypeResolves()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Cvolo.Compiler.Tooling.dll");
        if (!File.Exists(path))
        {
            return;
        }

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
        var server = typeof(LanguageServer.Protocol.LanguageServer).Assembly;
        var direct = DirectCompilerFamilyReferences(server);

        Assert.Equal(ManagedToolingAvailable ? ExpectedDirectCompilerReferences : [], direct);
    }

    [Fact]
    public void Core_ReferencesNoCompilerProtocolOrAntlrAssemblies()
    {
        var names = DirectReferenceNames(typeof(Core.DocumentStore).Assembly);

        Assert.DoesNotContain(names, n => n.StartsWith("Cvolo.", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Antlr4.Runtime", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
    }

    [Fact]
    public void Cvolo_ReferencesExactlyCompilerTooling_AmongCompilerFamily()
    {
        var cvolo = typeof(Cvolo.CvoloLanguageBackend).Assembly;
        var direct = DirectCompilerFamilyReferences(cvolo);

        Assert.Equal(ManagedToolingAvailable ? ExpectedDirectCompilerReferences : [], direct);
        Assert.DoesNotContain(DirectReferenceNames(cvolo), n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
    }

    [Fact]
    public void Server_ReferencesNoAntlrLlmOrCodegenAssemblies()
    {
        var server = typeof(LanguageServer.Protocol.LanguageServer).Assembly;
        var names = DirectReferenceNames(server);

        Assert.DoesNotContain(names, n => n.Contains("Antlr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("LLVM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("CodeGen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CvoloAdapter_ReferencesNoAntlrAssembly()
    {
        var cvolo = typeof(Cvolo.CvoloLanguageBackend).Assembly;
        var names = DirectReferenceNames(cvolo);

        Assert.DoesNotContain(names, n => n.Contains("Antlr", StringComparison.OrdinalIgnoreCase));
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
