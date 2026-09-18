param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('zip','tar.gz')][string]$ArchiveExtension,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$stage = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-ls-release-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

try {
    Get-ChildItem -LiteralPath $PublishDir -Force | Copy-Item -Destination $stage -Recurse -Force
    Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $stage 'bundle-manifest.json') -Force

    $base = "cvolo-language-server-$Version-$Rid"
    if ($ArchiveExtension -eq 'zip') {
        $archive = Join-Path $OutputDir "$base.zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
    } else {
        $archive = Join-Path $OutputDir "$base.tar.gz"
        tar -C $stage -czf $archive .
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    }

    $manifestHash = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        (Join-Path $OutputDir "$base.manifest.sha256"),
        "$manifestHash  bundle-manifest.json`n",
        [Text.UTF8Encoding]::new($false)
    )
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
