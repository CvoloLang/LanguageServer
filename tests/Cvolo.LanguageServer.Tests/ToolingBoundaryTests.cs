using System.Reflection;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

public class ToolingBoundaryTests
{
    private static readonly string[] ExpectedDirectCompilerReferences = [];

    [Fact]
    public void DirectCompilerReferences_AreAbsent()
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
