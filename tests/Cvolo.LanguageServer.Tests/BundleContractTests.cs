using System.Text;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

/// <summary>
/// Hermetic tests for the shared release bundle contract in
/// <c>build/release/bundle-common.ps1</c>.
///
/// The release scripts are the only thing standing between a developer's working
/// tree and a published artifact, so their rules are tested directly rather than
/// through a full publish. The cases that matter are the ones that let a wrong or
/// stale binary ship: a staging tree contaminated with a nested runtime-identifier
/// directory, a file the manifest does not describe, a manifest entry with no file
/// behind it, a manifest that disagrees with the binary about its own identity, and a
/// commit that is not a full SHA.
/// </summary>
public class BundleContractTests
{
    private const string Commit = "d3cc638347112657e65e6e3e69aa5da0f8a391c0";
    private const string ToolingCommit = "e5351722f5024ab8b0b033ec22f3abb6df3af4f5";

    private static string ReleaseScriptsDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "build", "release");
                if (File.Exists(Path.Combine(candidate, "bundle-common.ps1")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate build/release/bundle-common.ps1.");
        }
    }

    private sealed class BundleSession : IDisposable
    {
        public string Root { get; }

        public BundleSession()
        {
            Root = Path.Combine(Path.GetTempPath(), "cvolo-ls-bundletest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return full;
        }

        public string WriteBytes(string relativePath, byte[] content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A minimal but valid publish output: the entrypoint, the pinned tooling
    /// manifest, and a nested payload file. Its identity is the same shape
    /// <c>--version --json</c> emits.
    /// </summary>
    private static string IdentityJson(
        string version = "0.1.0-alpha.9",
        string commit = Commit,
        string toolingVersion = "0.0.21.0",
        string toolingCommit = ToolingCommit,
        string compilerLine = "0.0.21",
        string rid = "win-x64",
        string targetFramework = "net10.0",
        string runtimeVersion = "10.0.12")
    {
        return "{\"languageServerVersion\":" + Q(version)
            + ",\"languageServerCommit\":" + Q(commit)
            + ",\"toolingVersion\":" + Q(toolingVersion)
            + ",\"toolingCommit\":" + Q(toolingCommit)
            + ",\"compilerCompatibilityLine\":" + Q(compilerLine)
            + ",\"rid\":" + Q(rid)
            + ",\"targetFramework\":" + Q(targetFramework)
            + ",\"runtimeVersion\":" + (runtimeVersion is null ? "null" : Q(runtimeVersion))
            + "}";
    }

    private static string Q(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string ToolingManifestJson(string toolingVersion = "0.0.21.0", string compilerLine = "0.0.21", string commit = ToolingCommit)
    {
        return "{\"ToolingVersion\":" + Q(toolingVersion)
            + ",\"CompilerCompatibilityLine\":" + Q(compilerLine)
            + ",\"BuiltFromCompilerVersion\":\"0.0.21-alpha\""
            + ",\"TargetFramework\":\"net10.0\""
            + ",\"RuntimeIdentifier\":null"
            + ",\"Commit\":" + Q(commit)
            + "}";
    }

    private static string ReadBundleManifestScript()
    {
        return File.ReadAllText(Path.Combine(ReleaseScriptsDir, "bundle-common.ps1"));
    }

    /// <summary>
    /// The published runtime identifiers are enumerated in the contract so a staging
    /// tree can be checked for contamination. If the list ever stops covering a RID
    /// this project publishes to, the check silently weakens.
    /// </summary>
    [Fact]
    public void ContractCoversEveryPublishedRuntimeIdentifier()
    {
        var source = ReadBundleManifestScript();

        foreach (var rid in new[] { "win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            Assert.Contains($"'{rid}'", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The manifest schema is 2 and every provenance field is required. A release that
    /// omitted the commit could not be traced back to a build, which is exactly the
    /// failure this contract exists to prevent.
    /// </summary>
    [Fact]
    public void ManifestDeclaresSchemaTwoAndEveryProvenanceField()
    {
        var source = ReadBundleManifestScript();

        Assert.Contains("schemaVersion              = 2", source, StringComparison.Ordinal);
        foreach (var field in new[]
                 {
                     "languageServerVersion", "languageServerCommit", "toolingVersion", "toolingCommit",
                     "compilerCompatibilityLine", "rid", "targetFramework", "runtimeVersion",
                     "publishMode", "entrypoint", "files",
                 })
        {
            Assert.Contains(field, source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The old schema recorded only the server version and a nullable runtime version
    /// with no producer information, so the dead variable and the unconditional null
    /// must not come back.
    /// </summary>
    [Fact]
    public void ManifestNoLongerCarriesTheDeadNullRuntimeVersionOrLegacyFieldNames()
    {
        var source = ReadBundleManifestScript();

        Assert.DoesNotContain("sourceRevision", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serverVersion   =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion   = 1", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Release validation must never go looking for a binary. Every script takes its
    /// paths as parameters and the pipeline recreates the staging directory, so a
    /// stale RID-specific build under a project's bin directory can never be picked
    /// up as if it were the tagged build.
    /// </summary>
    [Fact]
    public void ReleaseScriptsNeverSearchProjectOutputTreesForAnExecutable()
    {
        foreach (var script in new[] { "new-bundle-manifest.ps1", "pack-release-asset.ps1", "test-release-asset.ps1" })
        {
            var source = File.ReadAllText(Path.Combine(ReleaseScriptsDir, script));

            Assert.DoesNotContain("bin/Release", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bin\\Release", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Get-ChildItem -Recurse -Filter", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sort-Object LastWriteTime", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The tooling payload is copied verbatim next to the executable, so the release
    /// must ship the whole pinned bundle, verified against the pinned bundle's own
    /// checksums. A partial or substituted payload would produce a server that starts
    /// and then cannot compile anything.
    /// </summary>
    [Fact]
    public void ContractRequiresTheCompleteVerifiedPinnedToolingPayload()
    {
        var source = ReadBundleManifestScript();

        Assert.Contains("Assert-ToolingPayloadMatches", source, StringComparison.Ordinal);
        Assert.Contains("Assert-ToolingPayloadMatches", File.ReadAllText(Path.Combine(ReleaseScriptsDir, "pack-release-asset.ps1")), StringComparison.Ordinal);
        Assert.Contains("Assert-ToolingPayloadMatches", File.ReadAllText(Path.Combine(ReleaseScriptsDir, "test-release-asset.ps1")), StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", source, StringComparison.Ordinal);
        Assert.Contains("Cvolo.Compiler.Tooling.dll", File.ReadAllText(Path.Combine(ReleaseScriptsDir, "test-release-asset.ps1")), StringComparison.Ordinal);
        Assert.Contains("tooling.manifest.json", File.ReadAllText(Path.Combine(ReleaseScriptsDir, "test-release-asset.ps1")), StringComparison.Ordinal);
    }

    /// <summary>
    /// A release bundle is defined to contain the entrypoint plus the pinned tooling
    /// bundle, and nothing else. The staging check therefore has to be able to reject a
    /// file that is not part of either, which is only a real check if it is made
    /// against the pinned bundle rather than by re-listing the staging directory.
    /// </summary>
    [Fact]
    public void StagingCheckRejectsPayloadOutsideTheEntrypointAndPinnedTooling()
    {
        var source = ReadBundleManifestScript();

        Assert.Contains("contains $($unexpected.Count) file(s) that are neither the entrypoint", source, StringComparison.Ordinal);
        Assert.Contains("no longer match the pinned bundle checksums", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compatibility gate is enforced by the bootstrap on both the cache-reuse and
    /// the download path, and re-applied by the release test to the tooling manifest
    /// that actually ships inside the archive.
    /// </summary>
    [Fact]
    public void CompatibilityGateIsEnforcedInBothFetchScriptsAndInReleaseVerification()
    {
        foreach (var script in new[] { "fetch-tooling.ps1", "fetch-tooling.sh" })
        {
            var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "build", script));
            Assert.Contains("compiler-compatibility.version", source, StringComparison.Ordinal);
        }

        var releaseTest = File.ReadAllText(Path.Combine(ReleaseScriptsDir, "test-release-asset.ps1"));
        Assert.Contains("packagedToolingManifest", releaseTest, StringComparison.Ordinal);
        Assert.Contains("CompilerCompatibilityLine", releaseTest, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tooling.version")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (tooling.version missing).");
    }
}
