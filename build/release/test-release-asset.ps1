param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ExpectedServerVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedToolingVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedCompilerLine
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 deadlocks on ReadLineAsync().Wait(); use blocking reads with a
# native watchdog that kills the child process when the read deadline expires.
if (-not ('CvoloJsonRpcWatchdog' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Threading;

public sealed class CvoloJsonRpcWatchdog : IDisposable
{
    private readonly Timer _timer;
    private readonly int _processId;

    public bool TimedOut { get; private set; }

    public CvoloJsonRpcWatchdog(int processId, int timeoutMs)
    {
        _processId = processId;
        _timer = new Timer(OnElapsed, null, timeoutMs, Timeout.Infinite);
    }

    private void OnElapsed(object state)
    {
        TimedOut = true;
        try { Process.GetProcessById(_processId).Kill(); } catch { }
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { }
    }
}
'@
}

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

function Read-JsonRpcFrame([IO.StreamReader]$reader, [Diagnostics.Process]$process, [int]$TimeoutMs) {
    $watchdog = [CvoloJsonRpcWatchdog]::new($process.Id, $TimeoutMs)
    try {
        $length = -1
        while ($true) {
            $line = $reader.ReadLine()
            if ($null -eq $line) {
                return $null
            }
            if ($line -eq '') { break }
            if ($line -match '(?i)^Content-Length:\s*(\d+)\s*$') { $length = [int]$Matches[1] }
        }

        if ($length -lt 0) { throw 'JSON-RPC frame is missing a Content-Length header' }

        $buffer = New-Object 'char[]' $length
        $read = 0
        while ($read -lt $length) {
            $count = $reader.Read($buffer, $read, $length - $read)
            if ($count -le 0) {
                return $null
            }
            $read += $count
        }

        $body = -join $buffer
        $frame = $body | ConvertFrom-Json
        Write-Host ("[smoke] frame id={0} method={1}" -f $frame.id, $frame.method)
        return $frame
    }
    finally {
        $watchdog.Dispose()
    }
}

function Wait-ForFrame([IO.StreamReader]$reader, [Diagnostics.Process]$process, [scriptblock]$predicate, [string]$description, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $remaining = [int](($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        if ($remaining -le 0) { break }

        $frame = Read-JsonRpcFrame -reader $reader -process $process -TimeoutMs $remaining
        if ($null -eq $frame) { break }
        if (& $predicate $frame) { return $frame }
    }

    throw "Timed out waiting for $description"
}

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$stage = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-ls-smoke-" + [guid]::NewGuid().ToString('N'))
$workspace = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-ls-workspace-" + [guid]::NewGuid().ToString('N'))

$process = $null
$logPath = $null
$stderrTask = $null
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

    $logPath = Join-Path $workspace 'server.log'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $exe
    $process.StartInfo.Arguments = "--stdio --log `"$logPath`""
    $process.StartInfo.WorkingDirectory = $stage
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.CreateNoWindow = $true

    if (-not $process.Start()) { throw 'Failed to start packaged language server' }

    $stdoutReader = [IO.StreamReader]::new($process.StandardOutput.BaseStream)
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $docPath = Join-Path $workspace 'main.cvl'
    Set-Content -LiteralPath $docPath -Value "int Main() {`n    return 0;`n}" -NoNewline
    Set-Content -LiteralPath (Join-Path $workspace 'App.cvlproj') -Value '<Project><ItemGroup /></Project>' -NoNewline
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

    Wait-ForFrame -reader $stdoutReader -process $process -description 'initialize response' -predicate { param($frame) $frame.id -eq 1 } | Out-Null
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

    Wait-ForFrame -reader $stdoutReader -process $process -description 'compiler-backed diagnostics' -predicate {
        param($frame)
        $frame.method -eq 'textDocument/publishDiagnostics' -and $frame.params.uri -eq $docUri -and @($frame.params.diagnostics).Count -gt 0
    } | Out-Null

    Send-JsonRpc $process ([ordered]@{ jsonrpc = '2.0'; id = 2; method = 'shutdown'; params = $null })
    Wait-ForFrame -reader $stdoutReader -process $process -description 'shutdown response' -predicate { param($frame) $frame.id -eq 2 } | Out-Null
    Send-JsonRpc $process ([ordered]@{ jsonrpc = '2.0'; method = 'exit'; params = $null })

    if (-not $process.WaitForExit(10000)) {
        try { $process.Kill() } catch { }
        throw 'Packaged language server did not exit after shutdown/exit'
    }
    if ($process.ExitCode -ne 0) { throw "Packaged language server exited with $($process.ExitCode)." }

    $stderrText = $stderrTask.GetAwaiter().GetResult()
    if ($stderrText -match 'Cvolo\.Compiler\.Tooling\.dll is unavailable') { throw "Tooling unavailable warning was emitted: $stderrText" }
    $logText = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' }
    if ($logText -notmatch 'Cvolo\.Compiler\.Tooling .* loaded successfully') { throw "Tooling load success was not logged. log: $logText" }

    Write-Output "Release smoke passed for $Rid (server $ExpectedServerVersion, tooling $ExpectedToolingVersion, compiler-line $ExpectedCompilerLine)."
}
catch {
    if ($null -ne $process) {
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
    }
    $capturedLog = if ($logPath -and (Test-Path -LiteralPath $logPath)) { Get-Content -LiteralPath $logPath -Raw } else { '(no log file)' }
    $capturedErr = ''
    if ($null -ne $stderrTask) { try { $capturedErr = $stderrTask.GetAwaiter().GetResult() } catch { } }
    throw "$_`n--- server log ---`n$capturedLog`n--- server stderr ---`n$capturedErr"
}
finally {
    if ($null -ne $process) {
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
        try { $process.Dispose() } catch { }
    }
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
}
