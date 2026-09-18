param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ExpectedServerVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedToolingVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedCompilerLine
)

$ErrorActionPreference = 'Stop'

function ConvertTo-JsonBytes([object]$value) {
    $json = $value | ConvertTo-Json -Depth 20 -Compress
    [Text.Encoding]::UTF8.GetBytes($json)
}

function Send-JsonRpc([object]$process, [object]$message) {
    $payload = ConvertTo-JsonBytes $message
    $header = [Text.Encoding]::ASCII.GetBytes("Content-Length: $($payload.Length)`r`n`r`n")
    $process.StandardInput.BaseStream.Write($header, 0, $header.Length)
    $process.StandardInput.BaseStream.Write($payload, 0, $payload.Length)
    $process.StandardInput.BaseStream.Flush()
}

function Get-JsonRpcFrames([string]$text) {
    $text = $text.Replace("`r`n", "`n")
    $frames = @()
    $offset = 0
    while ($true) {
        $headerEnd = $text.IndexOf("`n`n", $offset, [StringComparison]::Ordinal)
        if ($headerEnd -lt 0) { break }

        $header = $text.Substring($offset, $headerEnd - $offset)
        if ($header -notmatch '(?im)^Content-Length:\s*(\d+)\s*$') {
            throw "Malformed JSON-RPC frame header: $header"
        }

        $length = [int]$Matches[1]
        $bodyStart = $headerEnd + 2
        if ($text.Length -lt $bodyStart + $length) { break }

        $body = $text.Substring($bodyStart, $length)
        $frames += ($body | ConvertFrom-Json)
        $offset = $bodyStart + $length
    }

    return $frames
}

function Wait-Until([scriptblock]$Predicate, [string]$Description, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $result = & $Predicate
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Description"
}

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$stage = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-ls-smoke-" + [guid]::NewGuid().ToString('N'))
$workspace = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-ls-workspace-" + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $stage, $workspace | Out-Null
    if ($archive.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $stage -Force
    }
    elseif ($archive.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)) {
        tar -C $stage -xzf $archive
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    }
    else {
        throw "Unsupported archive extension: $archive"
    }

    $exeName = if ($Rid -eq 'win-x64') { 'cvolo-language-server.exe' } else { 'cvolo-language-server' }
    $exe = Join-Path $stage $exeName
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing packaged executable: $exe" }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'Cvolo.Compiler.Tooling.dll') -PathType Leaf)) { throw 'Missing packaged Cvolo.Compiler.Tooling.dll' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'tooling.manifest.json') -PathType Leaf)) { throw 'Missing packaged tooling.manifest.json' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -PathType Leaf)) { throw 'Missing packaged SHA256SUMS.txt' }

    if ($Rid -ne 'win-x64') {
        chmod +x $exe
    }

    $versionOutput = (& $exe --version 2>&1 | Out-String).Trim()
    if ($versionOutput -match 'Cvolo\.Compiler\.Tooling\.dll is unavailable') { throw "Tooling unavailable during --version: $versionOutput" }
    $versionLines = @($versionOutput -split "`r?`n" | Where-Object { $_ -ne '' })
    if ($versionLines.Count -lt 3) { throw "Unexpected --version output: $versionOutput" }
    if (-not $versionLines[0].StartsWith("cvolo-language-server $ExpectedServerVersion", [StringComparison]::Ordinal)) { throw "Unexpected server version line: $($versionLines[0])" }
    if ($versionLines[1] -ne "tooling $ExpectedToolingVersion") { throw "Unexpected tooling version line: $($versionLines[1])" }
    if ($versionLines[2] -ne "compiler-line $ExpectedCompilerLine") { throw "Unexpected compiler line: $($versionLines[2])" }

    $stdout = [Text.StringBuilder]::new()
    $stderr = [Text.StringBuilder]::new()
    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $exe
    $process.StartInfo.Arguments = '--stdio'
    $process.StartInfo.WorkingDirectory = $stage
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.EnableRaisingEvents = $true
    $process.add_OutputDataReceived({ if ($null -ne $EventArgs.Data) { [void]$stdout.AppendLine($EventArgs.Data) } })
    $process.add_ErrorDataReceived({ if ($null -ne $EventArgs.Data) { [void]$stderr.AppendLine($EventArgs.Data) } })

    if (-not $process.Start()) { throw 'Failed to start packaged language server' }
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()

    $docPath = Join-Path $workspace 'main.cvl'
    Set-Content -LiteralPath $docPath -Value "int Main() {`n    return 0;`n}" -NoNewline
    $workspaceUri = ([Uri]$workspace).AbsoluteUri
    $docUri = ([Uri]$docPath).AbsoluteUri

    Send-JsonRpc $process ([ordered]@{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = [ordered]@{
            processId = $PID
            workspaceFolders = @([ordered]@{ uri = $workspaceUri; name = 'release-smoke' })
            capabilities = [ordered]@{}
        }
    })

    Wait-Until { @(Get-JsonRpcFrames $stdout.ToString() | Where-Object { $_.id -eq 1 }).Count -gt 0 } 'initialize response' | Out-Null
    Send-JsonRpc $process ([ordered]@{ jsonrpc = '2.0'; method = 'initialized'; params = [ordered]@{} })
    Send-JsonRpc $process ([ordered]@{
        jsonrpc = '2.0'
        method = 'textDocument/didOpen'
        params = [ordered]@{ textDocument = [ordered]@{ uri = $docUri; languageId = 'cvolo'; version = 1; text = "int Main() {`n    return 0;`n}" } }
    })

    Send-JsonRpc $process ([ordered]@{
        jsonrpc = '2.0'
        method = 'textDocument/didChange'
        params = [ordered]@{ textDocument = [ordered]@{ uri = $docUri; version = 2 }; contentChanges = @([ordered]@{ text = 'int Main( { return 0; }' }) }
    })

    Wait-Until {
        $frames = Get-JsonRpcFrames $stdout.ToString()
        $diagnostics = @($frames | Where-Object { $_.method -eq 'textDocument/publishDiagnostics' -and $_.params.uri -eq $docUri -and @($_.params.diagnostics).Count -gt 0 })
        $diagnostics.Count -gt 0
    } 'compiler-backed diagnostics' | Out-Null

    Send-JsonRpc $process ([ordered]@{ jsonrpc = '2.0'; id = 2; method = 'shutdown'; params = $null })
    Wait-Until { @(Get-JsonRpcFrames $stdout.ToString() | Where-Object { $_.id -eq 2 }).Count -gt 0 } 'shutdown response' | Out-Null
    Send-JsonRpc $process ([ordered]@{ jsonrpc = '2.0'; method = 'exit'; params = $null })

    if (-not $process.WaitForExit(10000)) {
        $process.Kill($true)
        throw 'Packaged language server did not exit after shutdown/exit'
    }
    if ($process.ExitCode -ne 0) { throw "Packaged language server exited with $($process.ExitCode). stderr: $stderr" }

    $stderrText = $stderr.ToString()
    if ($stderrText -match 'Cvolo\.Compiler\.Tooling\.dll is unavailable') { throw "Tooling unavailable warning was emitted: $stderrText" }
    if ($stderrText -notmatch 'Cvolo\.Compiler\.Tooling .* loaded successfully') { throw "Tooling load success was not logged. stderr: $stderrText" }
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
}
