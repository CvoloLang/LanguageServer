<#
    fetch-tooling.ps1 - bootstrap the pinned Cvolo.Compiler.Tooling bundle

    TEMPORARY INFRASTRUCTURE. This is NOT a package manager. It downloads a
    single pinned release, verifies its checksums and manifest, and publishes
    it atomically under artifacts/tooling/<version>/. No version resolution,
    no fallback to another version, no unverified cache reuse.

    Exit codes: 0 success (cache reused or provisioned), non-zero failure.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$ArtifactsRoot = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# NOTE: $PSScriptRoot is NOT available inside [CmdletBinding()] param defaults in
# Windows PowerShell 5.1 (it is set only after parameter binding). Resolve it here
# instead, using $MyInvocation.MyCommand.Path as a fallback.
if ([string]::IsNullOrEmpty($RepoRoot)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrEmpty($scriptDir) -and $MyInvocation.MyCommand.Path) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    if ([string]::IsNullOrEmpty($scriptDir)) {
        Write-Err "Could not determine the repository root; run from the repo or pass -RepoRoot explicitly."
        exit 1
    }
    $RepoRoot = Split-Path -Parent $scriptDir
}
if ([string]::IsNullOrEmpty($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $RepoRoot 'artifacts'
}

function Write-Err([string]$msg) { Write-Host "[fetch-tooling] $msg" -ForegroundColor Red }
function Write-Info([string]$msg) { Write-Host "[fetch-tooling] $msg" -ForegroundColor Cyan }

# SHA-256 via .NET directly. Get-FileHash lives in a PowerShell module that is
# not always available in the constrained session used by the hermetic tests on
# CI runners, so the bootstrap must not depend on it.
function Get-Sha256Hex([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($path)
        try {
            return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

# ---------------------------------------------------------------------------
# Read and validate the pinned version
# ---------------------------------------------------------------------------
$versionFile = Join-Path $RepoRoot 'tooling.version'
$toolingVersion = $null
if (Test-Path -LiteralPath $versionFile) {
    $toolingVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($toolingVersion)) {
    Write-Err "tooling.version is missing or empty under $RepoRoot"
    exit 1
}
if ($toolingVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?([\-+][0-9A-Za-z.\-]+)?$') {
    Write-Err "tooling.version '$toolingVersion' is not a pinned concrete version"
    exit 1
}

$artifactsDir = $ArtifactsRoot
$toolingRoot = Join-Path $artifactsDir 'tooling'
$bundleDir = Join-Path $toolingRoot $toolingVersion
$propsFile = Join-Path $artifactsDir 'tooling-dir.props'
$zipUrl = "https://github.com/IgorShaposhnikov/Cvolo/releases/download/tooling-$toolingVersion/CvoloLanguageServerTooling-$toolingVersion.zip"
$zipShaUrl = "$zipUrl.sha256"

$sha256SumsFile = Join-Path $bundleDir 'SHA256SUMS.txt'
$manifestFile = Join-Path $bundleDir 'tooling.manifest.json'
$toolingDll = Join-Path $bundleDir 'Cvolo.Compiler.Tooling.dll'

# ---------------------------------------------------------------------------
# Verification
# ---------------------------------------------------------------------------
function Test-Checksums([string]$dir, [string]$sumsPath) {
    $script:CheckReason = $null
    try {
        # Reject BOM and CRLF: canonical SHA256SUMS.txt is UTF-8 without BOM,
        # LF-only. (Get-Content below would silently normalize both away.)
        $sumsBytes = [System.IO.File]::ReadAllBytes($sumsPath)
        if ($sumsBytes.Length -ge 3 -and $sumsBytes[0] -eq 0xEF -and $sumsBytes[1] -eq 0xBB -and $sumsBytes[2] -eq 0xBF) {
            $script:CheckReason = 'SHA256SUMS.txt has a UTF-8 BOM'
            return $false
        }
        if ([Array]::IndexOf($sumsBytes, [byte]0x0D) -ge 0) {
            $script:CheckReason = 'SHA256SUMS.txt contains CR bytes (must be LF-only)'
            return $false
        }
        # Strict canonical SHA256SUMS.txt format (compiler-generated, e.g.
        # "sha256sum" output): exactly 64 LOWERCASE hex chars, EXACTLY two
        # spaces, then a /-separated relative path. Malformed lines, uppercase
        # hashes, wrong separators, extra whitespace, and non-canonical paths
        # are rejected outright - never normalized.
        $expected = [System.Collections.Generic.List[string]]::new()
        $ordinalLookup = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
        $previousPath = $null
        foreach ($line in (Get-Content -LiteralPath $sumsPath)) {
            if ($line -notmatch '^[0-9a-f]{64}  [^ ].*$') {
                $script:CheckReason = "SHA256SUMS.txt line is not canonical: '$line'"
                return $false
            }
            $hash = $line.Substring(0, 64)
            $rel = $line.Substring(66)
            if ($rel.Contains('\') -or $rel.StartsWith('./') -or $rel.StartsWith('../')) {
                $script:CheckReason = "SHA256SUMS.txt path is not canonical: '$rel'"
                return $false
            }
            # Ordinal entry order: every path must be strictly greater than the previous one.
            if ($null -ne $previousPath -and [string]::CompareOrdinal($rel, $previousPath) -le 0) {
                $script:CheckReason = "SHA256SUMS.txt entries are not strictly ordinal-sorted at '$rel'"
                return $false
            }
            $previousPath = $rel
            $expected.Add($rel)
            $ordinalLookup[$rel] = $hash
        }
        # Canonicalize the directory through the filesystem so its form matches
        # the FullName reported by Get-ChildItem. A short (8.3) artifacts root
        # would otherwise make the prefix-length relative path computation wrong.
        $sumsDir = (Get-Item -LiteralPath (Split-Path -Parent $sumsPath)).FullName
        $sums = Get-ChildItem -LiteralPath $sumsDir -Recurse -File |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' }
        if ($sums.Count -ne $expected.Count) {
            $script:CheckReason = "file count mismatch: on disk $($sums.Count), listed $($expected.Count)"
            return $false
        }
        foreach ($file in $sums) {
            $rel = $file.FullName.Substring($sumsDir.Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $ordinalLookup.ContainsKey($rel)) {
                $script:CheckReason = "on-disk file '$rel' is not listed in SHA256SUMS.txt"
                return $false
            }
            $actual = Get-Sha256Hex $file.FullName
            if ($actual -ne $ordinalLookup[$rel]) {
                $script:CheckReason = "hash mismatch for '$rel'"
                return $false
            }
        }
        return $true
    }
    catch {
        $script:CheckReason = "checksum verification threw: $($_.Exception.Message)"
        return $false
    }
}

function Test-CachedBundle {
    if (-not (Test-Path -LiteralPath $sha256SumsFile) -or
        -not (Test-Path -LiteralPath $manifestFile) -or
        -not (Test-Path -LiteralPath $toolingDll)) {
        $script:CheckReason = 'bundle is missing SHA256SUMS.txt, tooling.manifest.json, or Cvolo.Compiler.Tooling.dll'
        return $false
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
        if (-not (Test-ManifestFields $manifest)) {
            $script:CheckReason = 'tooling.manifest.json does not satisfy the producer contract'
            return $false
        }
    }
    catch {
        $script:CheckReason = "reading tooling.manifest.json threw: $($_.Exception.Message)"
        return $false
    }
    return (Test-Checksums $bundleDir $sha256SumsFile)
}

# Accept the compiler compatibility line. LSP-0 has no server-side policy yet:
# any well-formed numeric line from the manifest is accepted; it is surfaced to
# the build through the generated props so later increments can gate on it.
function Test-Compatible([string]$line) {
    return $line -match '^\d+\.\d+(\.\d+)*$'
}

# The producer manifest contract: version identity, compiler compatibility line,
# the compiler revision it was built from, its target framework, RID-neutrality
# (RuntimeIdentifier must be null), and an immutable source commit.
function Test-ManifestFields($manifest) {
    if ($manifest.ToolingVersion -ne $toolingVersion) { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.CompilerCompatibilityLine)) { return $false }
    if ([string]$manifest.CompilerCompatibilityLine -notmatch '^\d+\.\d+(\.\d+)*$') { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.BuiltFromCompilerVersion)) { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.TargetFramework)) { return $false }
    if (-not ($manifest.PSObject.Properties.Name -contains 'RuntimeIdentifier')) { return $false }
    if ($null -ne $manifest.RuntimeIdentifier) { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.Commit)) { return $false }
    return $true
}

function Write-Props($manifest) {
    $content = "<Project>`n  <PropertyGroup>`n" +
        "    <CvoloToolingVersion>$($manifest.ToolingVersion)</CvoloToolingVersion>`n" +
        "    <CvoloToolingDir>`$(MSBuildThisFileDirectory)tooling/$($manifest.ToolingVersion)</CvoloToolingDir>`n" +
        "    <CompilerCompatLine>$($manifest.CompilerCompatibilityLine)</CompilerCompatLine>`n" +
        "  </PropertyGroup>`n</Project>`n"
    [System.IO.File]::WriteAllText($propsFile, $content, [System.Text.UTF8Encoding]::new($false))
}

# ---------------------------------------------------------------------------
# Reuse an existing valid cache without touching the network. An existing
# target that is invalid/conflicting is a HARD failure: it is never moved
# aside, repaired, replaced, or overwritten.
# ---------------------------------------------------------------------------
if (Test-Path -LiteralPath $bundleDir) {
    if (Test-CachedBundle) {
        Write-Info "Reusing verified tooling cache at $bundleDir"
        if (Test-Path -LiteralPath $manifestFile) {
            $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
            Write-Props $manifest
        }
        exit 0
    }
    Write-Err "Tooling cache at $bundleDir exists but is INVALID or conflicting."
    if ($script:CheckReason) { Write-Err "Reason: $($script:CheckReason)" }
    Write-Err "This process will NOT move it aside, repair it, replace it, or overwrite it."
    Write-Err "Delete or fix the cache manually, then re-run fetch-tooling."
    exit 1
}

# ---------------------------------------------------------------------------
# Download (with retries) and verify a fresh bundle
# ---------------------------------------------------------------------------
$pidSuffix = $PID
$tempZip = Join-Path $toolingRoot ".tmp-$toolingVersion-$pidSuffix.zip"
$tempSha = Join-Path $toolingRoot ".tmp-$toolingVersion-$pidSuffix.zip.sha256"
$tempExtract = Join-Path $toolingRoot ".tmp-$toolingVersion-$pidSuffix"

New-Item -ItemType Directory -Path $toolingRoot -Force | Out-Null

function Download-Bundle {
    param([string]$url, [string]$dest)
    $wc = New-Object System.Net.WebClient
    try {
        $wc.DownloadFile($url, $dest)
    }
    catch [System.Net.WebException] {
        $resp = $_.Response
        if ($null -ne $resp -and $resp.StatusCode -ne [System.Net.HttpStatusCode]::OK) {
            $code = [int]$resp.StatusCode
            if ($code -ge 400 -and $code -lt 500) {
                throw (New-Object System.Management.Automation.RuntimeException("PermanentlyUnavailable:$url (HTTP $code)"))
            }
        }
        throw
    }
    finally {
        $wc.Dispose()
    }
}

function Get-RemoteFile {
    param([string]$url, [string]$dest, [string]$label)
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            Write-Info "Downloading $label (attempt $attempt/4): $url"
            Download-Bundle $url $dest
            return $true
        }
        catch {
            if ($_.Message -like 'PermanentlyUnavailable*') {
                Write-Err "Tooling $toolingVersion is not available at $url (HTTP 4xx, permanent)."
                Write-Err "No retries were attempted and no fallback version or unverified cache is used."
                return $false
            }
            Write-Err "Download failed: $($_.Message)"
            if ($attempt -lt 4) {
                Start-Sleep -Seconds 2
            }
        }
    }
    return $false
}

if (-not (Get-RemoteFile $zipUrl $tempZip 'tooling archive')) {
    exit 1
}

# Verify the downloaded archive against the published .sha256 sidecar asset
# BEFORE it is extracted or used. No unverified fallback is permitted.
if (-not (Get-RemoteFile $zipShaUrl $tempSha 'tooling archive checksum')) {
    Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
    exit 1
}

$shaMatch = [regex]::Match((Get-Content -LiteralPath $tempSha -Raw).Trim(), '^([0-9a-fA-F]{64})')
if (-not $shaMatch.Success) {
    Write-Err "Published checksum asset $zipShaUrl is malformed."
    Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue
    exit 1
}
$expectedZipHash = $shaMatch.Groups[1].Value.ToLowerInvariant()
$actualZipHash = Get-Sha256Hex $tempZip
if ($actualZipHash -ne $expectedZipHash) {
    Write-Err "Downloaded tooling archive does not match the published checksum asset."
    Write-Err "  expected: $expectedZipHash"
    Write-Err "  actual:   $actualZipHash"
    Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue
    exit 1
}

try {
    Expand-Archive -LiteralPath $tempZip -DestinationPath $tempExtract -Force

    # Normalize a single top-level directory if the zip wraps the bundle.
    $bundleRoot = $tempExtract
    if (-not (Test-Path (Join-Path $bundleRoot 'tooling.manifest.json'))) {
        $children = Get-ChildItem -LiteralPath $bundleRoot -Force
        if ($children.Count -eq 1 -and (Get-Item -LiteralPath $children[0].FullName).PSIsContainer) {
            $bundleRoot = $children[0].FullName
        }
    }

    $tempSums = Join-Path $bundleRoot 'SHA256SUMS.txt'
    $tempManifest = Join-Path $bundleRoot 'tooling.manifest.json'
    $tempDll = Join-Path $bundleRoot 'Cvolo.Compiler.Tooling.dll'

    if ((-not (Test-Path -LiteralPath $tempSums) -or
        -not (Test-Path -LiteralPath $tempManifest) -or
        -not (Test-Path -LiteralPath $tempDll))) {
        throw "Downloaded bundle is missing required files (manifest/checksums/Cvolo.Compiler.Tooling.dll)"
    }
    if (-not (Test-Checksums $bundleRoot $tempSums)) {
        throw "Downloaded bundle failed SHA256 checksum verification"
    }

    $manifest = Get-Content -LiteralPath $tempManifest -Raw | ConvertFrom-Json
    if (-not (Test-ManifestFields $manifest)) {
        throw "Manifest does not satisfy the producer contract (ToolingVersion/CompilerCompatibilityLine/BuiltFromCompilerVersion/TargetFramework/RuntimeIdentifier/Commit)"
    }
}
catch {
    Write-Err "Tooling bundle is unusable: $($_.Exception.Message)"
    Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}

# ---------------------------------------------------------------------------
# Publish atomically. If the target appeared meanwhile, apply the same 3-way
# policy: verified => reuse; present-but-invalid => fail without touching it;
# absent => this process's verified temp dir wins the publish. The target is
# never moved aside, repaired, replaced, or overwritten during the race.
# ---------------------------------------------------------------------------
function Test-PublishTarget {
    if (Test-Path -LiteralPath $bundleDir) {
        if (Test-CachedBundle) {
            Write-Info "Another process provisioned a verified cache; reusing $bundleDir"
            Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $manifestFile) {
                $concurrentManifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
                Write-Props $concurrentManifest
            }
            exit 0
        }
        Write-Err "Concurrent tooling provisioning produced an unverified/conflicting cache at $bundleDir."
        Write-Err "This process did NOT move it aside, repair it, replace it, or overwrite it."
        Write-Err "Re-run fetch-tooling after the conflict is resolved."
        Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
        exit 1
    }
}

Test-PublishTarget
try {
    Move-Item -LiteralPath $tempExtract -Destination $bundleDir
}
catch {
    # Destination appeared between the check and the move: apply the same policy.
    Test-PublishTarget
    Write-Err "Concurrent tooling provisioning could not be resolved: $($_.Exception.Message)"
    Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue

$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
Write-Props $manifest
Write-Info "Provisioned verified tooling $toolingVersion at $bundleDir"
exit 0
