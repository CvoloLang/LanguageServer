param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ServerVersion,
    [Parameter(Mandatory = $true)][string]$SourceRevision,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path $PublishDir).Path
$entrypoint = if ($Rid -eq 'win-x64') { 'cvolo-language-server.exe' } else { 'cvolo-language-server' }
$entryPath = Join-Path $publish $entrypoint

if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
    throw "Expected entrypoint not found: $entryPath"
}

$files = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
if ($files.Count -ne 1) {
    throw "Single-file release requires exactly one published file; found $($files.Count)."
}

$runtimeVersion = (& $entryPath --version 2>$null | Out-String).Trim()
# The server's current --version contract exposes server/tooling/compiler-line,
# not the embedded .NET runtime patch. Keep runtimeVersion explicitly nullable
# until the producer has a stable build-time source for it.
$manifest = [ordered]@{
    schemaVersion   = 1
    serverVersion   = $ServerVersion
    sourceRevision  = $SourceRevision
    targetFramework = 'net10.0'
    runtimeVersion  = $null
    rid             = $Rid
    publishMode     = 'self-contained-single-file'
    entrypoint      = $entrypoint
    files           = @(
        [ordered]@{
            path       = $entrypoint
            size       = (Get-Item -LiteralPath $entryPath).Length
            sha256     = (Get-FileHash -LiteralPath $entryPath -Algorithm SHA256).Hash.ToLowerInvariant()
            executable = $true
        }
    )
}

$outDir = Split-Path -Parent $OutputPath
if ($outDir) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($OutputPath, $json + "`n", [Text.UTF8Encoding]::new($false))
