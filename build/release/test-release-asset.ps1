<#
    test-release-asset.ps1 - verify a packaged release archive, end to end

    Everything is verified against the artifact that a user would download. The
    archive is extracted into a private staging directory, the executable under test
    is the one extracted from that archive (never anything left in the project
    bin/ tree), and the bundled manifest is checked to describe the extracted
    contents exactly.

    The identity is then confirmed three ways, which must all agree:
      1. the shipped bundle-manifest.json,
      2. 'cvolo-language-server --version'        (human, four lines),
      3. 'cvolo-language-server --version --json' (machine, one object),
    plus the pinned tooling bundle's own manifest, which must name the same tooling
    version and compiler compatibility line. Finally the server is started over
    stdio JSON-RPC and must produce compiler-backed diagnostics.
#>
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ToolingDir,
    [Parameter(Mandatory = $true)][string]$ExpectedServerVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedLanguageServerCommit,
    [Parameter(Mandatory = $true)][string]$ExpectedToolingVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedCompilerLine
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'bundle-common.ps1')
. (Join-Path $PSScriptRoot 'file-uri.ps1')

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

    $exeName = Get-BundleEntrypointName -Rid $Rid
    $exe = Join-Path $stage $exeName
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing packaged executable: $exe" }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'Cvolo.Compiler.Tooling.dll') -PathType Leaf)) { throw 'Missing packaged Cvolo.Compiler.Tooling.dll' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -PathType Leaf)) { throw 'Missing packaged SHA256SUMS.txt' }

    if ($Rid -ne 'win-x64') {
        chmod +x $exe
    }

    # ---------------------------------------------------------------------
    # The shipped manifest must describe the extracted archive exactly.
    # ---------------------------------------------------------------------
    $manifestPath = Join-Path $stage 'bundle-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Missing packaged bundle-manifest.json' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Test-BundleManifestSchema -Manifest $manifest -Rid $Rid
    Assert-BundleStagingLayout -Root $stage
    Assert-ToolingPayloadMatches -Root $stage -ToolingDir $ToolingDir -Rid $Rid -AllowExtra @('bundle-manifest.json')
    Assert-ManifestMatchesDirectory -Root $stage -Manifest $manifest -AllowExtra @('bundle-manifest.json')

    foreach ($pair in @(
            @{ Field = 'languageServerVersion'; Expected = $ExpectedServerVersion },
            @{ Field = 'languageServerCommit'; Expected = $ExpectedLanguageServerCommit },
            @{ Field = 'toolingVersion'; Expected = $ExpectedToolingVersion },
            @{ Field = 'compilerCompatibilityLine'; Expected = $ExpectedCompilerLine },
            @{ Field = 'rid'; Expected = $Rid })) {
        $actual = Get-BundleManifestField -Source $manifest -Name $pair.Field
        if ($actual -ne $pair.Expected) {
            throw "Packaged bundle-manifest.json $($pair.Field) is '$actual' but the release expects '$($pair.Expected)'."
        }
    }

    # The pinned tooling bundle ships inside the archive, so the compatibility gate
    # is re-applied to the files actually being released, not just to the ones the
    # build consumed: the packaged tooling manifest must name the same tooling
    # version and the same compiler compatibility line.
    $packagedToolingManifest = Get-Content -LiteralPath (Join-Path $stage 'tooling.manifest.json') -Raw | ConvertFrom-Json
    if ([string]$packagedToolingManifest.ToolingVersion -ne [string]$manifest.toolingVersion) {
        throw "Packaged tooling.manifest.json ToolingVersion '$($packagedToolingManifest.ToolingVersion)' does not match the bundle manifest toolingVersion '$($manifest.toolingVersion)'."
    }
    if ([string]$packagedToolingManifest.CompilerCompatibilityLine -ne [string]$manifest.compilerCompatibilityLine) {
        throw "Packaged tooling.manifest.json CompilerCompatibilityLine '$($packagedToolingManifest.CompilerCompatibilityLine)' does not match the bundle manifest compilerCompatibilityLine '$($manifest.compilerCompatibilityLine)'."
    }
    if ([string]$packagedToolingManifest.Commit -ne [string]$manifest.toolingCommit) {
        throw "Packaged tooling.manifest.json Commit '$($packagedToolingManifest.Commit)' does not match the bundle manifest toolingCommit '$($manifest.toolingCommit)'."
    }

    # ---------------------------------------------------------------------
    # Identity: human form, then machine form, then all three against each other.
    # ---------------------------------------------------------------------
    $versionOutput = (& $exe --version 2>&1 | Out-String).Trim()
    if ($versionOutput -match 'Cvolo\.Compiler\.Tooling\.dll is unavailable') { throw "Tooling unavailable during --version: $versionOutput" }
    $versionLines = @($versionOutput -split "`r?`n" | Where-Object { $_ -ne '' })
    if ($versionLines.Count -ne 4) { throw "Expected exactly 4 --version lines, got $($versionLines.Count): $versionOutput" }
    $expectedVersionLines = @(
        "Cvolo Language Server $ExpectedServerVersion",
        "Commit: $ExpectedLanguageServerCommit",
        "Tooling: $ExpectedToolingVersion",
        "Compiler compatibility: $ExpectedCompilerLine"
    )
    for ($i = 0; $i -lt 4; $i++) {
        if ($versionLines[$i] -ne $expectedVersionLines[$i]) {
            throw "--version line $($i + 1) is '$($versionLines[$i])' but must be '$($expectedVersionLines[$i])'."
        }
    }

    $jsonOutput = (& $exe --version --json 2>&1 | Out-String).Trim()
    if ($jsonOutput -match 'Cvolo\.Compiler\.Tooling\.dll is unavailable') { throw "Tooling unavailable during --version --json: $jsonOutput" }
    $identity = $null
    try {
        $identity = $jsonOutput | ConvertFrom-Json
    }
    catch {
        throw "--version --json did not emit a single JSON object: $jsonOutput"
    }
    Assert-BundleIdentity -Identity $identity `
        -ExpectedLanguageServerVersion ([string]$manifest.languageServerVersion) `
        -ExpectedLanguageServerCommit ([string]$manifest.languageServerCommit) `
        -ExpectedToolingVersion ([string]$manifest.toolingVersion) `
        -ExpectedToolingCommit ([string]$manifest.toolingCommit) `
        -ExpectedCompilerCompatibilityLine ([string]$manifest.compilerCompatibilityLine) `
        -ExpectedRid $Rid `
        -ExpectedTargetFramework ([string]$manifest.targetFramework)

    # --json without --version is a usage error, not a request to start the server.
    $jsonOnly = [Diagnostics.Process]::new()
    $jsonOnly.StartInfo.FileName = $exe
    $jsonOnly.StartInfo.Arguments = '--json'
    $jsonOnly.StartInfo.UseShellExecute = $false
    $jsonOnly.StartInfo.RedirectStandardOutput = $true
    $jsonOnly.StartInfo.RedirectStandardError = $true
    $jsonOnly.StartInfo.CreateNoWindow = $true
    try {
        [void]$jsonOnly.Start()
        [void]$jsonOnly.StandardOutput.ReadToEnd()
        [void]$jsonOnly.StandardError.ReadToEnd()
        if (-not $jsonOnly.WaitForExit(20000)) { $jsonOnly.Kill() }
        if ($jsonOnly.ExitCode -eq 0) {
            throw "'--json' without '--version' was accepted; it must be rejected."
        }
    }
    finally {
        try { if (-not $jsonOnly.HasExited) { $jsonOnly.Kill() } } catch { }
        try { $jsonOnly.Dispose() } catch { }
    }

    $logPath = $null
    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $exe
    $process.StartInfo.Arguments = '--stdio --verbose'
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

    $workspaceUri = ConvertTo-FileUri -Path $workspace
    $docUri = ConvertTo-FileUri -Path $docPath
    foreach ($entry in @(@{ Name = 'workspace'; Uri = $workspaceUri }, @{ Name = 'document'; Uri = $docUri })) {
        if ($null -eq $entry.Uri) { throw "Release smoke $($entry.Name) URI is null." }
        if (-not $entry.Uri.IsAbsoluteUri) { throw "Release smoke $($entry.Name) URI is not absolute: '$($entry.Uri)'." }
        if ($entry.Uri.Scheme -ne [System.Uri]::UriSchemeFile) { throw "Release smoke $($entry.Name) URI scheme is '$($entry.Uri.Scheme)', expected 'file': '$($entry.Uri)'." }
    }

    $workspaceUriString = $workspaceUri.AbsoluteUri
    $docUriString = $docUri.AbsoluteUri

    Send-JsonRpc $process ([ordered]@{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = [ordered]@{
            processId = $PID
            workspaceFolders = @([ordered]@{ uri = $workspaceUriString; name = 'release-smoke' })
            capabilities = [ordered]@{}
        }
    })

    Wait-ForFrame -reader $stdoutReader -process $process -description 'initialize response' -predicate { param($frame) $frame.id -eq 1 } | Out-Null
    Send-JsonRpc $process ([ordered]@{ jsonrpc = '2.0'; method = 'initialized'; params = [ordered]@{} })
    Send-JsonRpc $process ([ordered]@{
        jsonrpc = '2.0'
        method = 'textDocument/didOpen'
        params = [ordered]@{ textDocument = [ordered]@{ uri = $docUriString; languageId = 'cvolo'; version = 1; text = "int Main() {`n    return 0;`n}" } }
    })
    Send-JsonRpc $process ([ordered]@{
        jsonrpc = '2.0'
        method = 'textDocument/didChange'
        params = [ordered]@{ textDocument = [ordered]@{ uri = $docUriString; version = 2 }; contentChanges = @([ordered]@{ text = 'int Main( { return 0; }' }) }
    })

    Wait-ForFrame -reader $stdoutReader -process $process -description 'compiler-backed diagnostics' -predicate {
        param($frame)
        $frame.method -eq 'textDocument/publishDiagnostics' -and $frame.params.uri -eq $docUriString -and @($frame.params.diagnostics).Count -gt 0
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
    if ($stderrText -notmatch 'Cvolo\.Compiler\.Tooling .* loaded successfully') { throw "Tooling load success was not logged. stderr: $stderrText" }

    Write-Output ("Release smoke passed for {0}: server {1}, commit {2}, tooling {3}, compiler line {4}, {5} verified files, runtime {6}." -f `
            $Rid, $manifest.languageServerVersion, $manifest.languageServerCommit, $manifest.toolingVersion, `
            $manifest.compilerCompatibilityLine, @($manifest.files).Count, $manifest.runtimeVersion)
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
