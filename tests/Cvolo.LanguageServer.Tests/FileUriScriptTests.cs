using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

/// <summary>
/// Hermetic tests for <c>build/release/file-uri.ps1</c>, the cross-platform
/// filesystem-path to <c>file:</c> URI helper used by the release smoke. The
/// helper must produce absolute, escaped file URIs for platform-native absolute
/// paths on Windows, Linux and macOS, and must fail loudly (never return null)
/// for invalid input.
/// </summary>
public class FileUriScriptTests
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

    private sealed record UriResult(string? AbsoluteUri, bool IsAbsoluteUri, string? Scheme, string? LocalPath);

    private static UriResult RunFileUriHelper(string path)
    {
        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "build", "release", "file-uri.ps1");
        Assert.True(File.Exists(helperPath), $"file-uri.ps1 not found at {helperPath}");

        var runnerPath = Path.Combine(Path.GetTempPath(), "cvolo-ls-fileuri-runner-" + Guid.NewGuid().ToString("N") + ".ps1");
        var runner = string.Join(
            '\n',
            "$ErrorActionPreference = 'Stop'",
            ". $env:CVOLO_FILEURI_HELPER",
            "$u = ConvertTo-FileUri -Path $env:CVOLO_FILEURI_PATH",
            "[ordered]@{ AbsoluteUri = $u.AbsoluteUri; IsAbsoluteUri = $u.IsAbsoluteUri; Scheme = $u.Scheme; LocalPath = $u.LocalPath } | ConvertTo-Json -Compress");

        File.WriteAllText(runnerPath, runner, new UTF8Encoding(false));

        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(runnerPath);
            }
            else
            {
                psi = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(runnerPath);
            }

            psi.Environment["CVOLO_FILEURI_HELPER"] = helperPath;
            psi.Environment["CVOLO_FILEURI_PATH"] = path;

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the PowerShell file-uri helper host.");
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

                throw new TimeoutException("file-uri helper test script timed out.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"file-uri helper failed with exit {process.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            }

            var json = stdout.Trim();
            var parsed = JsonSerializer.Deserialize<UriResult>(json)
                ?? throw new InvalidOperationException($"file-uri helper produced no JSON. stdout:\n{stdout}\nstderr:\n{stderr}");
            return parsed;
        }
        finally
        {
            File.Delete(runnerPath);
        }
    }

    private static void AssertAbsoluteFileUri(UriResult result, string sourcePath)
    {
        Assert.False(string.IsNullOrWhiteSpace(result.AbsoluteUri), "file URI must not be null or empty");
        Assert.True(result.IsAbsoluteUri, $"file URI must be absolute, got '{result.AbsoluteUri}'");
        Assert.Equal("file", result.Scheme);
        Assert.StartsWith("file:///", result.AbsoluteUri, StringComparison.Ordinal);

        var expected = Path.GetFullPath(sourcePath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Assert.Equal(expected, result.LocalPath, comparer);
    }

    [Fact]
    public void PathWithSpaces_ProducesEscapedAbsoluteFileUri()
    {
        var path = Path.Combine(Path.GetTempPath(), "cvolo ls ws " + Guid.NewGuid().ToString("N"), "main.cvl");

        var result = RunFileUriHelper(path);

        AssertAbsoluteFileUri(result, path);
        Assert.Contains("%20", result.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceDirectoryPath_ProducesAbsoluteFileUri()
    {
        var path = Path.Combine(Path.GetTempPath(), "cvolo-ls-workspace-" + Guid.NewGuid().ToString("N"));

        var result = RunFileUriHelper(path);

        AssertAbsoluteFileUri(result, path);
    }

    [Fact]
    public void WhitespacePath_FailsLoudly_InsteadOfReturningNull()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RunFileUriHelper("   "));
        Assert.Contains("empty path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
