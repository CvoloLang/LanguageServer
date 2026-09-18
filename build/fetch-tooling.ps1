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
if ($toolingVersion -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z.\-]+)?$') {
    Write-Err "tooling.version '$toolingVersion' is not a pinned concrete version"
    exit 1
}

$artifactsDir = $ArtifactsRoot
$toolingRoot = Join-Path $artifactsDir 'tooling'
$bundleDir = Join-Path $toolingRoot $toolingVersion
$propsFile = Join-Path $artifactsDir 'tooling-dir.props'
$zipUrl = "https://github.com/IgorShaposhnikov/Cvolo/releases/download/v0.0.5-alpha.1/CvoloLanguageServerTooling-$toolingVersion.zip"

$sha256SumsFile = Join-Path $bundleDir 'SHA256SUMS.txt'
$manifestFile = Join-Path $bundleDir 'tooling.manifest.json'
$toolingDll = Join-Path $bundleDir 'Cvolo.Compiler.Tooling.dll'

# ---------------------------------------------------------------------------
# Verification
# ---------------------------------------------------------------------------
function Test-Checksums([string]$dir, [string]$sumsPath) {
    try {
        # Reject BOM and CRLF: canonical SHA256SUMS.txt is UTF-8 without BOM,
        # LF-only. (Get-Content below would silently normalize both away.)
        $sumsBytes = [System.IO.File]::ReadAllBytes($sumsPath)
        if ($sumsBytes.Length -ge 3 -and $sumsBytes[0] -eq 0xEF -and $sumsBytes[1] -eq 0xBB -and $sumsBytes[2] -eq 0xBF) {
            return $false
        }
        if ([Array]::IndexOf($sumsBytes, [byte]0x0D) -ge 0) {
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
                return $false
            }
            $hash = $line.Substring(0, 64)
            $rel = $line.Substring(66)
            if ($rel.Contains('\') -or $rel.StartsWith('./') -or $rel.StartsWith('../')) {
                return $false
            }
            # Ordinal entry order: every path must be strictly greater than the previous one.
            if ($null -ne $previousPath -and [string]::CompareOrdinal($rel, $previousPath) -le 0) {
                return $false
            }
            $previousPath = $rel
            $expected.Add($rel)
            $ordinalLookup[$rel] = $hash
        }
        $sums = Get-ChildItem -LiteralPath (Split-Path -Parent $sumsPath) -Recurse -File |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' }
        if ($sums.Count -ne $expected.Count) { return $false }
        foreach ($file in $sums) {
            $rel = $file.FullName.Substring((Split-Path -Parent $sumsPath).Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $ordinalLookup.ContainsKey($rel)) { return $false }
            $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $ordinalLookup[$rel]) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-CachedBundle {
    if (-not (Test-Path -LiteralPath $sha256SumsFile) -or
        -not (Test-Path -LiteralPath $manifestFile) -or
        -not (Test-Path -LiteralPath $toolingDll)) { return $false }
    try {
        $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
        if ($manifest.ToolingVersion -ne $toolingVersion) { return $false }
        if ([string]::IsNullOrWhiteSpace($manifest.CompilerCompatibilityLine)) { return $false }
        if ($manifest.CompilerCompatibilityLine -notmatch '^\d+\.\d+(\.\d+)*$') { return $false }
    }
    catch { return $false }
    return (Test-Checksums $bundleDir $sha256SumsFile)
}

# Accept the compiler compatibility line. LSP-0 has no server-side policy yet:
# any well-formed numeric line from the manifest is accepted; it is surfaced to
# the build through the generated props so later increments can gate on it.
function Test-Compatible([string]$line) {
    return $line -match '^\d+\.\d+(\.\d+)*$'
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
    Write-Err "This process will NOT move it aside, repair it, replace it, or overwrite it."
    Write-Err "Delete or fix the cache manually, then re-run fetch-tooling."
    exit 1
}

# ---------------------------------------------------------------------------
# Download (with retries) and verify a fresh bundle
# ---------------------------------------------------------------------------
$pidSuffix = $PID
$tempZip = Join-Path $toolingRoot ".tmp-$toolingVersion-$pidSuffix.zip"
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

$downloaded = $false
$permanentError = $null
for ($attempt = 1; $attempt -le 4; $attempt++) {
    try {
        Write-Info "Downloading $zipUrl (attempt $attempt/4)"
        Download-Bundle $zipUrl $tempZip
        $downloaded = $true
        break
    }
    catch {
        if ($_.Message -like 'PermanentlyUnavailable*') {
            $permanentError = $_.Message
            break
        }
        Write-Err "Download failed: $($_.Message)"
        if ($attempt -lt 4) {
            Start-Sleep -Seconds 2
        }
    }
}

if (-not $downloaded) {
    if ($null -ne $permanentError) {
        Write-Err "Tooling $toolingVersion is not available at $zipUrl (HTTP 4xx, permanent)."
        Write-Err "No retries were attempted and no fallback version or unverified cache is used."
    }
    else {
        Write-Err "Tooling $toolingVersion could not be fetched from $zipUrl after retries."
    }
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
    if ($manifest.ToolingVersion -ne $toolingVersion) {
        throw "Manifest ToolingVersion '$($manifest.ToolingVersion)' does not match tooling.version '$toolingVersion'"
    }
    if (-not (Test-Compatible ([string]$manifest.CompilerCompatibilityLine))) {
        throw "Manifest CompilerCompatibilityLine '$($manifest.CompilerCompatibilityLine)' is not accepted"
    }
}
catch {
    Write-Err "Tooling bundle is unusable: $($_.Exception.Message)"
    Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
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
    Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue

$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
Write-Props $manifest
Write-Info "Provisioned verified tooling $toolingVersion at $bundleDir"
exit 0
