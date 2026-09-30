<#
    new-bundle-manifest.ps1 - create the schema 2 bundle manifest for one RID

    The manifest is a self-describing, machine-readable record of exactly what a
    release archive contains and which commit produced it. Every identity field is
    read from the executable being packaged and then cross-checked against the values
    the release pipeline independently derived, so the manifest can never describe a
    different build than the one in the archive.

    All paths are explicit. The caller owns the publish directory, recreates it
    immediately before publishing into it, and passes it in here; this script never
    searches bin/, obj/ or any previous artifacts tree for an executable.
#>
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ToolingDir,
    [Parameter(Mandatory = $true)][string]$ExpectedLanguageServerVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedLanguageServerCommit,
    [Parameter(Mandatory = $true)][string]$ExpectedToolingVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedToolingCommit,
    [Parameter(Mandatory = $true)][string]$ExpectedCompilerCompatibilityLine,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'bundle-common.ps1')

if ($ExpectedLanguageServerCommit -notmatch '^[0-9a-f]{40}$') {
    throw "ExpectedLanguageServerCommit '$ExpectedLanguageServerCommit' is not a full 40-character lowercase commit SHA."
}

$publish = (Resolve-Path -LiteralPath $PublishDir).Path
$entrypoint = Get-BundleEntrypointName -Rid $Rid
$entryPath = Join-Path $publish $entrypoint
if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
    throw "Expected entrypoint not found: $entryPath"
}

# The staging tree must be a clean single-RID publish output, and it must contain the
# whole pinned tooling payload. Both are checked before anything is written.
Assert-BundleStagingLayout -Root $publish
Assert-ToolingPayloadMatches -Root $publish -ToolingDir $ToolingDir -Rid $Rid

# The identity of the binary itself, and of the pinned tooling bundle it ships.
$identity = Read-BundleIdentity -EntryPath $entryPath
Assert-BundleIdentity -Identity $identity `
    -ExpectedLanguageServerVersion $ExpectedLanguageServerVersion `
    -ExpectedLanguageServerCommit $ExpectedLanguageServerCommit `
    -ExpectedToolingVersion $ExpectedToolingVersion `
    -ExpectedToolingCommit $ExpectedToolingCommit `
    -ExpectedCompilerCompatibilityLine $ExpectedCompilerCompatibilityLine `
    -ExpectedRid $Rid

$files = Get-BundleFileEntries -Root $publish
$manifest = New-BundleManifestDocument -Identity $identity -Rid $Rid -EntryPoint $entrypoint -Files $files

# The manifest must describe the directory it was taken from, exactly.
Assert-ManifestMatchesDirectory -Root $publish -Manifest $manifest
Test-BundleManifestSchema -Manifest $manifest -Rid $Rid | Out-Null

$outDir = Split-Path -Parent $OutputPath
if ($outDir) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($OutputPath, $json + "`n", [Text.UTF8Encoding]::new($false))

Write-Output ("bundle-manifest.json written: {0} ({1} files, commit {2}, tooling {3}@{4}, compiler line {5})" -f `
        $OutputPath, $files.Count, $manifest.languageServerCommit, $manifest.toolingVersion, `
        $manifest.toolingCommit, $manifest.compilerCompatibilityLine)
