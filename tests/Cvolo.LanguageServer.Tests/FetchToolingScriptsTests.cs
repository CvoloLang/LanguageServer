using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

/// <summary>
/// Hermetic tests for the fetch-tooling bootstrap scripts (PowerShell on
/// Windows, bash elsewhere). They provision a fake bundle under a TEMPORARY
/// artifacts root and run the script against it with --artifacts-root, so no
/// network access and no mutation of the real artifacts directory occur.
/// </summary>
public class FetchToolingScriptsTests
{
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tooling.version")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (tooling.version missing).");
    }

    private static string ToolingVersion { get; } = File.ReadAllText(Path.Combine(FindRepoRoot(), "tooling.version")).Trim();

    private static string CreateFakeArtifactsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cvolo-ls-fetchtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tooling", ToolingVersion));
        return root;
    }

    private static string BundleDir(string root) => Path.Combine(root, "tooling", ToolingVersion);

    private static void WriteManifest(string bundleDir, string toolingVersion, string compatLine)
    {
        var manifest =
            $"{{\"ToolingVersion\":\"{toolingVersion}\",\"CompilerCompatibilityLine\":\"{compatLine}\"," +
            "\"BuiltFromCompilerVersion\":\"0.0.4\",\"TargetFramework\":\"net10.0\",\"Commit\":\"test\"}";
        File.WriteAllText(Path.Combine(bundleDir, "tooling.manifest.json"), manifest, new UTF8Encoding(false));
    }

    private static void WriteDummyToolingDll(string bundleDir)
    {
        File.WriteAllBytes(
            Path.Combine(bundleDir, "Cvolo.Compiler.Tooling.dll"),
            Encoding.UTF8.GetBytes("dummy tooling dll for fetch-tooling tests"));
    }

    private static string BuildCanonicalSumsContent(string bundleDir)
    {
        var files = Directory.EnumerateFiles(bundleDir, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "SHA256SUMS.txt")
            .OrderBy(f => Path.GetRelativePath(bundleDir, f), StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(bundleDir, file).Replace('\\', '/');
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            sb.Append(hash).Append("  ").Append(rel).Append('\n');
        }

        return sb.ToString();
    }

    private static void WriteSums(string bundleDir, string content)
    {
        File.WriteAllText(Path.Combine(bundleDir, "SHA256SUMS.txt"), content, new UTF8Encoding(false));
    }

    private static string? ReadManifestVersion(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.TryGetProperty("ToolingVersion", out JsonElement value) ? value.GetString() : null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunFetchScript(string artifactsRoot)
    {
        var buildDir = Path.Combine(FindRepoRoot(), "build");
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(Path.Combine(buildDir, "fetch-tooling.ps1"));
            psi.ArgumentList.Add("-ArtifactsRoot");
            psi.ArgumentList.Add(artifactsRoot);
        }
        else
        {
            psi = new ProcessStartInfo("bash")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(Path.Combine(buildDir, "fetch-tooling.sh"));
            psi.ArgumentList.Add("--artifacts-root");
            psi.ArgumentList.Add(artifactsRoot);
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start fetch-tooling script.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("fetch-tooling test script timed out.");
        }

        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void ValidCanonicalBundle_IsReused_ExitsZero_AndWritesProps()
    {
        var root = CreateFakeArtifactsRoot();
        try
        {
            var bundle = BundleDir(root);
            WriteManifest(bundle, ToolingVersion, compatLine: "0.0");
            WriteDummyToolingDll(bundle);
            WriteSums(bundle, BuildCanonicalSumsContent(bundle));

            (var exit, var stdout, var stderr) = RunFetchScript(root);

            Assert.True(exit == 0, $"expected exit 0; got {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("Reusing verified tooling cache", stdout + stderr);

            var propsFile = Path.Combine(root, "tooling-dir.props");
            Assert.True(File.Exists(propsFile), "props file must be written on reuse");
            var props = File.ReadAllText(propsFile);
            Assert.Contains($"<CvoloToolingVersion>{ToolingVersion}</CvoloToolingVersion>", props);
            Assert.Contains($"<CvoloToolingDir>$(MSBuildThisFileDirectory)tooling/{ToolingVersion}</CvoloToolingDir>", props);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UppercaseHash_IsRejected_ExitsNonZero_AndPropsNotWritten()
    {
        var root = CreateFakeArtifactsRoot();
        try
        {
            var bundle = BundleDir(root);
            WriteManifest(bundle, ToolingVersion, "0.0");
            WriteDummyToolingDll(bundle);
            var canonical = BuildCanonicalSumsContent(bundle);
            var firstLine = canonical.Split('\n')[0];
            var upperFirstLine = firstLine[..64].ToUpperInvariant() + firstLine[64..];
            WriteSums(bundle, upperFirstLine + "\n");

            (var exit, var _, var _) = RunFetchScript(root);

            Assert.NotEqual(0, exit);
            Assert.False(File.Exists(Path.Combine(root, "tooling-dir.props")), "props must not be written for an invalid cache");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MalformedSeparator_IsRejected_ExitsNonZero()
    {
        var root = CreateFakeArtifactsRoot();
        try
        {
            var bundle = BundleDir(root);
            WriteManifest(bundle, ToolingVersion, "0.0");
            WriteDummyToolingDll(bundle);
            var canonical = BuildCanonicalSumsContent(bundle);
            var firstLine = canonical.Split('\n')[0];
            var singleSpaceLine = firstLine.Remove(65, 1); // drop one of the two spaces
            WriteSums(bundle, singleSpaceLine + "\n");

            (var exit, var _, var _) = RunFetchScript(root);

            Assert.NotEqual(0, exit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnorderedEntries_AreRejected_ExitsNonZero()
    {
        var root = CreateFakeArtifactsRoot();
        try
        {
            var bundle = BundleDir(root);
            WriteManifest(bundle, ToolingVersion, "0.0");
            WriteDummyToolingDll(bundle);
            var lines = BuildCanonicalSumsContent(bundle).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var reversed = string.Join('\n', lines.Reverse()) + "\n";
            WriteSums(bundle, reversed);

            (var exit, var _, var _) = RunFetchScript(root);

            Assert.NotEqual(0, exit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingInvalidBundle_FailsWithoutOverwrite_OrSideEffects()
    {
        var root = CreateFakeArtifactsRoot();
        try
        {
            var bundle = BundleDir(root);
            // conflicting version
            WriteManifest(bundle, toolingVersion: "9.9.9", compatLine: "0.0"); 
            WriteDummyToolingDll(bundle);
            WriteSums(bundle, BuildCanonicalSumsContent(bundle));
            var marker = Path.Combine(bundle, "keep-me.bin");
            File.WriteAllBytes(marker, [1, 2, 3]);

            (var exit, var stdout, var stderr) = RunFetchScript(root);

            Assert.NotEqual(0, exit);
            Assert.True(File.Exists(marker), "invalid existing cache must NOT be removed, repaired, or overwritten");
            Assert.Equal("9.9.9", ReadManifestVersion(Path.Combine(bundle, "tooling.manifest.json")));
            Assert.False(
                Directory.EnumerateDirectories(Path.Combine(root, "tooling"))
                    .Any(d => Path.GetFileName(d).StartsWith(".tmp-", StringComparison.Ordinal)),
                "no temporary/stale directories may be created around an invalid cache");
            Assert.False(File.Exists(Path.Combine(root, "tooling-dir.props")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}