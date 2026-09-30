using System.Text.Json;
using Cvolo.LanguageServer;
using Cvolo.LanguageServer.Cli;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

/// <summary>
/// The <c>--version</c> contract. Version output is the provenance surface a user,
/// a bug report and the release pipeline all read, so the shape is asserted exactly
/// rather than loosely. The four human lines and the JSON object must carry the same
/// values, and neither may claim a compiler-derived product version.
/// </summary>
public class ServerVersionOutputTests
{
    private static string CaptureStdout(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    private static string CaptureStdErr(Action action)
    {
        var original = Console.Error;
        using var writer = new StringWriter();
        try
        {
            Console.SetError(writer);
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void HumanVersionPrintsFourLabelledLines()
    {
        var output = CaptureStdout(ServerPipeline.PrintVersion);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();

        Assert.Equal(4, lines.Length);
        Assert.Equal($"Cvolo Language Server {ServerMetadata.ServerVersion}", lines[0]);
        Assert.Equal($"Commit: {ServerMetadata.LanguageServerCommit}", lines[1]);
        Assert.Equal($"Tooling: {ServerMetadata.ToolingVersion}", lines[2]);
        Assert.Equal($"Compiler compatibility: {ServerMetadata.CompilerCompatibilityLine}", lines[3]);
    }

    /// <summary>
    /// The product version is independent of the compiler line, so the banner must
    /// never carry the compiler line as a version, nor a "+&lt;sha&gt;" suffix that
    /// would make the product version differ from the tagged version.
    /// </summary>
    [Fact]
    public void HumanVersionIsAnIndependentCleanProductVersion()
    {
        var version = ServerMetadata.ServerVersion;

        Assert.DoesNotContain('+', version);
        Assert.False(version.StartsWith(ServerMetadata.CompilerCompatibilityLine + "-", StringComparison.Ordinal));
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$", version);
    }

    [Fact]
    public void JsonVersionIsOneObjectCarryingTheSameProvenance()
    {
        var output = CaptureStdout(ServerPipeline.PrintVersionJson);
        var trimmed = output.Trim();

        // Exactly one object, on one line, so a caller can parse it directly.
        Assert.DoesNotContain('\n', trimmed);

        using var document = JsonDocument.Parse(trimmed);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        Assert.Equal(ServerMetadata.ServerVersion, root.GetProperty("languageServerVersion").GetString());
        Assert.Equal(ServerMetadata.LanguageServerCommit, root.GetProperty("languageServerCommit").GetString());
        Assert.Equal(ServerMetadata.ToolingVersion, root.GetProperty("toolingVersion").GetString());
        Assert.Equal(ServerMetadata.ToolingCommit, root.GetProperty("toolingCommit").GetString());
        Assert.Equal(ServerMetadata.CompilerCompatibilityLine, root.GetProperty("compilerCompatibilityLine").GetString());
        Assert.Equal(ServerMetadata.TargetFramework, root.GetProperty("targetFramework").GetString());
        Assert.Equal(ServerMetadata.RuntimeIdentifier, root.GetProperty("rid").GetString());
    }

    /// <summary>
    /// The JSON field names are the release manifest's field names, so the manifest
    /// and the binary can be compared field by field. Renaming one without the other
    /// would silently break release verification.
    /// </summary>
    [Fact]
    public void JsonFieldNamesMatchTheBundleManifestContract()
    {
        var output = CaptureStdout(ServerPipeline.PrintVersionJson);

        foreach (var field in new[]
                 {
                     "languageServerVersion",
                     "languageServerCommit",
                     "toolingVersion",
                     "toolingCommit",
                     "compilerCompatibilityLine",
                     "rid",
                     "targetFramework",
                     "runtimeVersion",
                 })
        {
            Assert.Contains($"\"{field}\"", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void JsonVersionOmitsNothingButKeepsAnUnknownRuntimeNullable()
    {
        var output = CaptureStdout(ServerPipeline.PrintVersionJson);

        // runtimeVersion is genuinely nullable: a bundle that cannot determine the
        // runtime version must say so rather than guess, and the manifest schema
        // allows the same null.
        using var document = JsonDocument.Parse(output.Trim());
        var runtime = document.RootElement.GetProperty("runtimeVersion");
        Assert.True(
            runtime.ValueKind is JsonValueKind.String or JsonValueKind.Null,
            "runtimeVersion must be a string or null, never omitted or another type");
    }

    [Fact]
    public void VersionOptionIsPresentAndVerboseRemainsALoggingSwitch()
    {
        var command = new CvoloLanguageServerRootCommand();

        Assert.Contains(command.Options, option => option.Name == "--version");
        Assert.Contains(command.Options, option => option.Name == "--json");
        Assert.Contains(command.Options, option => option.Name == "--verbose");

        // --verbose is logging only. It must not double as version verbosity, which is
        // why the machine-readable form is --version --json.
        var verbose = command.Options.Single(option => option.Name == "--verbose");
        Assert.Equal("Enable verbose logging, including raw JSON-RPC payloads.", verbose.Description);
    }
}
