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

$files = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName)

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
    publishMode     = 'self-contained-with-tooling-bundle'
    entrypoint      = $entrypoint
    files           = @($files | ForEach-Object {
        $relativePath = $_.FullName.Substring($publish.Length).TrimStart([char[]]@([char]92, [char]47)).Replace([char]92, [char]47)
        [ordered]@{
            path       = $relativePath
            size       = $_.Length
            sha256     = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            executable = $relativePath -eq $entrypoint
        }
    })
}

$outDir = Split-Path -Parent $OutputPath
if ($outDir) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($OutputPath, $json + "`n", [Text.UTF8Encoding]::new($false))
