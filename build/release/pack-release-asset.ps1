<#
    pack-release-asset.ps1 - package one publish output into a release archive

    The archive is flat: every payload file plus bundle-manifest.json sit at the
    archive root, so the layout is identical on every platform and the manifest is
    always at a known location.

    Both inputs are explicit and are validated against each other before anything is
    written. The staged copy is checked to contain exactly the manifest's file list:
    a file that the manifest does not describe, or a manifest entry with no file
    behind it, fails the packaging rather than being shipped.
#>
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ToolingDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('zip', 'tar.gz')][string]$ArchiveExtension,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'bundle-common.ps1')

$publish = (Resolve-Path -LiteralPath $PublishDir).Path
$manifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

Test-BundleManifestSchema -Manifest $manifest -Rid $Rid

# The publish output must be exactly the entrypoint plus the pinned tooling bundle, and
# exactly what the manifest describes, before anything is copied.
Assert-BundleStagingLayout -Root $publish
Assert-ToolingPayloadMatches -Root $publish -ToolingDir $ToolingDir -Rid $Rid
Assert-ManifestMatchesDirectory -Root $publish -Manifest $manifest

$entrypoint = [string]$manifest.entrypoint
$entryPath = Join-Path $publish $entrypoint
if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
    throw "Expected entrypoint not found: $entryPath"
}

# Re-read the identity from the exact executable that is about to be archived.
Assert-BundleIdentity -Identity (Read-BundleIdentity -EntryPath $entryPath) `
    -ExpectedLanguageServerVersion ([string]$manifest.languageServerVersion) `
    -ExpectedLanguageServerCommit ([string]$manifest.languageServerCommit) `
    -ExpectedToolingVersion ([string]$manifest.toolingVersion) `
    -ExpectedToolingCommit ([string]$manifest.toolingCommit) `
    -ExpectedCompilerCompatibilityLine ([string]$manifest.compilerCompatibilityLine) `
    -ExpectedRid $Rid `
    -ExpectedTargetFramework ([string]$manifest.targetFramework)

$stage = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-ls-release-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

try {
    Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $stage -Recurse -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage 'bundle-manifest.json') -Force

    # The staged copy is the archive's content, so it gets the same structural and
    # content checks as the publish output, with the manifest itself allowed as the
    # one file it does not list.
    Assert-BundleStagingLayout -Root $stage
    Assert-ToolingPayloadMatches -Root $stage -ToolingDir $ToolingDir -Rid $Rid -AllowExtra @('bundle-manifest.json')
    Assert-ManifestMatchesDirectory -Root $stage -Manifest $manifest -AllowExtra @('bundle-manifest.json')

    $base = "cvolo-language-server-$Version-$Rid"
    if ($ArchiveExtension -eq 'zip') {
        $archive = Join-Path $OutputDir "$base.zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        $archive = Join-Path $OutputDir "$base.tar.gz"
        tar -C $stage -czf $archive .
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    }

    $manifestHash = Get-BundleFileSha256 -Path $manifestPath
    $sidecar = Join-Path $OutputDir "$base.manifest.sha256"
    [IO.File]::WriteAllText($sidecar, "$manifestHash  bundle-manifest.json`n", [Text.UTF8Encoding]::new($false))

    Write-Output ("Packed {0} ({1} files) and checksum sidecar for {2}." -f `
            (Split-Path -Leaf $archive), @($manifest.files).Count, $Rid)
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
