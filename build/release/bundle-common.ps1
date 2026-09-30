<#
    bundle-common.ps1 - shared release bundle contract

    These functions are dot-sourced by new-bundle-manifest.ps1,
    pack-release-asset.ps1 and test-release-asset.ps1, and are exercised directly
    by the hermetic tests in tests/Cvolo.LanguageServer.Tests.

    Everything here is deterministic and path-explicit by design. A release is built
    into one owned staging directory that the caller recreates immediately before
    publishing into it, and every path a script touches is passed in or derived from
    the manifest. No function ever searches bin/, obj/ or a previous artifacts tree
    looking for "the newest" or "the first matching" executable, because a stale
    single-file host left in a RID-specific bin subdirectory would otherwise be
    packaged and shipped under a tag it was never built from.
#>

# The runtime identifiers this product publishes. A directory named after one of
# these inside a publish output means the staging tree is contaminated - a raw
# 'dotnet publish' tree copied on top of itself, or a bundle staged inside a bundle.
$script:BundleKnownRids = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')

$script:BundleSha256 = '^[0-9a-f]{40}$'

function Get-BundleEntrypointName {
    <#
        .SYNOPSIS
            The published entrypoint file name for a RID.
    #>
    param([Parameter(Mandatory = $true)][string]$Rid)
    if ($Rid -eq 'win-x64') { return 'cvolo-language-server.exe' }
    return 'cvolo-language-server'
}

function Get-BundleFileSha256 {
    <#
        .SYNOPSIS
            Lowercase SHA-256 of a file, computed with .NET directly.
        .DESCRIPTION
            Get-FileHash lives in a PowerShell module that is not always available
            in the constrained session used on CI runners, so the release scripts
            must not depend on it.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
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

function Get-BundleRelativePath {
    <#
        .SYNOPSIS
            A /-separated path relative to a canonicalized root.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Full
    )
    $relative = $Full.Substring($Root.Length).TrimStart([char[]]@([char]92, [char]47))
    return $relative.Replace([char]92, [char]47)
}

function Get-BundleFileEntries {
    <#
        .SYNOPSIS
            Every regular file under Root as ordered {path,size,sha256} maps, sorted
            byte-wise by relative path so the manifest is reproducible.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$Exclude = @()
    )
    $excludeSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Exclude) { [void]$excludeSet.Add($item) }

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relative = Get-BundleRelativePath -Root $Root -Full $file.FullName
        if ($excludeSet.Contains($relative)) { continue }
        $entries.Add([ordered]@{
                path   = $relative
                size   = $file.Length
                sha256 = Get-BundleFileSha256 -Path $file.FullName
            })
    }

    # Sort byte-wise with an ordinal comparer. Sort-Object, even with -CaseSensitive,
    # uses a culture-aware comparison whose ordering of '/' against letters and of
    # upper against lower case depends on the runner's locale; the manifest would then
    # not be reproducible across CI machines.
    $byPath = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $entries) { $byPath[$entry.path] = $entry }
    $paths = [string[]]($entries | ForEach-Object { $_.path })
    [Array]::Sort($paths, [System.StringComparer]::Ordinal)

    $ordered = [object[]]::new($paths.Length)
    for ($i = 0; $i -lt $paths.Length; $i++) { $ordered[$i] = $byPath[$paths[$i]] }
    return , $ordered
}

function Assert-BundleStagingLayout {
    <#
        .SYNOPSIS
            Reject a staging tree that is not a clean single-RID publish output.
        .DESCRIPTION
            Fails when any directory under Root is named after one of the published
            RIDs. Such a directory means the tree was assembled by copying one publish
            over another, and it is how a bundle once ended up containing a nested
            win-x64/ and a stray sibling osx-arm64/.
    #>
    param([Parameter(Mandatory = $true)][string]$Root)
    $contaminated = @(Get-ChildItem -LiteralPath $Root -Directory -Recurse |
        Where-Object { $script:BundleKnownRids -contains $_.Name })
    if ($contaminated.Count -gt 0) {
        $names = ($contaminated | ForEach-Object { Get-BundleRelativePath -Root $Root -Full $_.FullName }) -join ', '
        throw "Staging directory '$Root' contains nested runtime-identifier directories ($names). The publish output must be a single-RID tree created fresh for this release."
    }
}

function Assert-ToolingPayloadMatches {
    <#
        .SYNOPSIS
            The staging tree must be exactly the entrypoint plus the pinned tooling bundle.
        .DESCRIPTION
            This is the check that makes release staging deterministic rather than
            merely fresh. A release bundle is defined to contain exactly two things:
            the single-file server executable this build produced, and a byte-for-byte
            copy of the pinned, checksum-verified tooling bundle. Anything else in the
            staging tree is unreported payload from some earlier build, so it is
            rejected by name. Missing tooling files, and tooling files whose content
            no longer matches the pinned bundle's own SHA256SUMS.txt, are rejected too.

            Checking membership against the pinned bundle is deliberately not the same
            as re-listing the staging directory: enumerating the directory would make
            any stale file part of the manifest by construction and would therefore
            never fail.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ToolingDir,
        [Parameter(Mandatory = $true)][string]$Rid,
        [string[]]$AllowExtra = @()
    )
    if (-not (Test-Path -LiteralPath $ToolingDir -PathType Container)) {
        throw "Pinned tooling directory not found: $ToolingDir"
    }

    $allowSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $AllowExtra) { [void]$allowSet.Add($item) }

    # Canonicalize through the filesystem: a relative or 8.3 path would otherwise be
    # sliced at the wrong offset when computing paths relative to the tooling root.
    $toolingRoot = (Resolve-Path -LiteralPath $ToolingDir).Path
    $entrypoint = Get-BundleEntrypointName -Rid $Rid

    # The pinned bundle's own checksum list is the authority for the tooling payload.
    $expectedHashes = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    $toolingSums = Join-Path $toolingRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $toolingSums -PathType Leaf)) {
        throw "Pinned tooling bundle has no SHA256SUMS.txt: $toolingSums"
    }
    foreach ($line in Get-Content -LiteralPath $toolingSums) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Pinned tooling SHA256SUMS.txt line is not canonical: '$line'" }
        $expectedHashes[$Matches[2]] = $Matches[1]
    }

    $toolingRelative = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $missing = [System.Collections.Generic.List[string]]::new()
    $corrupt = [System.Collections.Generic.List[string]]::new()
    $unexpected = [System.Collections.Generic.List[string]]::new()

    foreach ($file in Get-ChildItem -LiteralPath $toolingRoot -File -Recurse) {
        $relative = Get-BundleRelativePath -Root $toolingRoot -Full $file.FullName
        [void]$toolingRelative.Add($relative)
        if ($relative -eq 'SHA256SUMS.txt') { continue }
        $staged = Join-Path $Root ($relative.Replace([char]47, [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $staged -PathType Leaf)) {
            $missing.Add($relative)
            continue
        }
        if ($expectedHashes.ContainsKey($relative) -and $expectedHashes[$relative] -ne (Get-BundleFileSha256 -Path $staged)) {
            $corrupt.Add($relative)
        }
    }

    $rootPath = (Resolve-Path -LiteralPath $Root).Path
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relative = Get-BundleRelativePath -Root $rootPath -Full $file.FullName
        if ($relative -ne $entrypoint -and -not $toolingRelative.Contains($relative) -and -not $allowSet.Contains($relative)) {
            $unexpected.Add($relative)
        }
    }

    if ($unexpected.Count -gt 0) {
        throw "Staging directory '$Root' contains $($unexpected.Count) file(s) that are neither the entrypoint '$entrypoint' nor part of the pinned tooling bundle: $(($unexpected | Select-Object -First 10) -join ', '). Stale payload from an earlier build must never reach a release."
    }
    if ($missing.Count -gt 0) {
        throw "Staging directory '$Root' is missing $($missing.Count) file(s) of the pinned tooling bundle: $(($missing | Select-Object -First 10) -join ', ')"
    }
    if ($corrupt.Count -gt 0) {
        throw "Staging directory '$Root' has $($corrupt.Count) tooling file(s) that no longer match the pinned bundle checksums: $(($corrupt | Select-Object -First 10) -join ', ')"
    }
}

function Read-BundleIdentity {
    <#
        .SYNOPSIS
            The identity the built executable reports about itself.
        .DESCRIPTION
            Runs '<entrypoint> --version --json' and returns the parsed object. The
            manifest is a transcription of the binary being packaged rather than of
            what the pipeline believed it built, so a stale or mismatched executable
            is detected here instead of shipping.
    #>
    param([Parameter(Mandatory = $true)][string]$EntryPath)

    $raw = (& $EntryPath --version --json 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        throw "'$EntryPath --version --json' produced no output (exit $LASTEXITCODE)."
    }

    try {
        return $raw | ConvertFrom-Json
    }
    catch {
        throw "'$EntryPath --version --json' did not emit a single JSON object: $raw"
    }
}

function Test-BundleManifestFieldPresent {
    <#
        .SYNOPSIS
            True when the manifest carries the field at all, even if its value is $null.
        .DESCRIPTION
            A manifest is only ever in one of two shapes: the in-memory ordered
            dictionary produced by New-BundleManifestDocument, or an object parsed
            back from the written JSON. The check has to work for both, and it must
            distinguish "absent" from "present and null" because runtimeVersion is
            legitimately null when the runtime does not report a version.
    #>
    param(
        [Parameter(Mandatory = $true)]$Source,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Source -is [System.Collections.IDictionary]) { return $Source.Contains($Name) }
    return $null -ne $Source.PSObject.Properties[$Name]
}

function Get-BundleManifestField {
    <#
        .SYNOPSIS
            A required string field of a manifest or identity object.
    #>
    param(
        [Parameter(Mandatory = $true)]$Source,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if (-not (Test-BundleManifestFieldPresent -Source $Source -Name $Name)) { return $null }
    $value = if ($Source -is [System.Collections.IDictionary]) { $Source[$Name] } else { $Source.PSObject.Properties[$Name].Value }
    if ($null -eq $value) { return $null }
    return [string]$value
}

function Assert-BundleIdentity {
    <#
        .SYNOPSIS
            The executable's own identity must equal the expected release identity.
    #>
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$ExpectedLanguageServerVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedLanguageServerCommit,
        [Parameter(Mandatory = $true)][string]$ExpectedToolingVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedToolingCommit,
        [Parameter(Mandatory = $true)][string]$ExpectedCompilerCompatibilityLine,
        [Parameter(Mandatory = $true)][string]$ExpectedRid,
        [string]$ExpectedTargetFramework
    )

    $checks = [ordered]@{
        languageServerVersion      = $ExpectedLanguageServerVersion
        languageServerCommit       = $ExpectedLanguageServerCommit
        toolingVersion             = $ExpectedToolingVersion
        toolingCommit              = $ExpectedToolingCommit
        compilerCompatibilityLine  = $ExpectedCompilerCompatibilityLine
        rid                        = $ExpectedRid
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetFramework)) {
        $checks['targetFramework'] = $ExpectedTargetFramework
    }

    foreach ($name in $checks.Keys) {
        $actual = Get-BundleManifestField -Source $Identity -Name $name
        if ($actual -ne $checks[$name]) {
            throw "Built executable reports $name '$actual' but the release expects '$($checks[$name])'. The staging directory does not contain the build this tag names."
        }
    }

    $commit = Get-BundleManifestField -Source $Identity -Name 'languageServerCommit'
    if ($commit -notmatch $script:BundleSha256) {
        throw "Built executable reports languageServerCommit '$commit', which is not a full 40-character commit SHA. A release must never be published without verifiable provenance."
    }
}

function New-BundleManifestDocument {
    <#
        .SYNOPSIS
            Assemble the schema 2 bundle manifest.
        .DESCRIPTION
            Every identity field is copied from the executable that is about to be
            packaged, so the manifest and the binary cannot disagree. Field order is
            fixed and the file list is byte-wise sorted, so two runs of the same
            inputs produce byte-identical output.
    #>
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$Rid,
        [Parameter(Mandatory = $true)][string]$EntryPoint,
        [Parameter(Mandatory = $true)]$Files
    )

    $runtimeVersion = Get-BundleManifestField -Source $Identity -Name 'runtimeVersion'
    $runtimeField = if ([string]::IsNullOrWhiteSpace($runtimeVersion)) { $null } else { $runtimeVersion }

    return [ordered]@{
        schemaVersion              = 2
        languageServerVersion      = Get-BundleManifestField -Source $Identity -Name 'languageServerVersion'
        languageServerCommit       = Get-BundleManifestField -Source $Identity -Name 'languageServerCommit'
        toolingVersion             = Get-BundleManifestField -Source $Identity -Name 'toolingVersion'
        toolingCommit              = Get-BundleManifestField -Source $Identity -Name 'toolingCommit'
        compilerCompatibilityLine  = Get-BundleManifestField -Source $Identity -Name 'compilerCompatibilityLine'
        rid                        = $Rid
        targetFramework            = Get-BundleManifestField -Source $Identity -Name 'targetFramework'
        runtimeVersion             = $runtimeField
        publishMode                = 'self-contained-single-file-with-tooling-payload'
        entrypoint                 = $EntryPoint
        files                      = @($Files | ForEach-Object {
                [ordered]@{
                    path       = $_.path
                    size       = $_.size
                    sha256     = $_.sha256
                    executable = $_.path -eq $EntryPoint
                }
            })
    }
}

function Assert-ManifestMatchesDirectory {
    <#
        .SYNOPSIS
            The directory contents and the manifest file list must be exactly equal.
        .DESCRIPTION
            Fails on a file present on disk but absent from the manifest (an
            unreported, unverified payload), on a manifest entry with no file behind
            it, and on any size or SHA-256 disagreement. This is the check that keeps
            stale files out of a release: a leftover from an earlier publish fails the
            build instead of being shipped.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Manifest,
        [string[]]$AllowExtra = @()
    )

    $allowSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $AllowExtra) { [void]$allowSet.Add($item) }

    $expected = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($file in @($Manifest.files)) {
        $relative = [string]$file.path
        if ($expected.ContainsKey($relative)) {
            throw "Manifest lists '$relative' more than once."
        }
        $expected[$relative] = $file
    }

    $onDisk = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relative = Get-BundleRelativePath -Root $Root -Full $file.FullName
        $onDisk[$relative] = $file.FullName
    }

    $unreported = @($onDisk.Keys | Where-Object { -not $expected.ContainsKey($_) -and -not $allowSet.Contains($_) } | Sort-Object -CaseSensitive)
    if ($unreported.Count -gt 0) {
        throw "Directory '$Root' contains $($unreported.Count) file(s) the manifest does not list: $(($unreported | Select-Object -First 10) -join ', '). Stale content cannot be shipped."
    }

    $absent = @($expected.Keys | Where-Object { -not $onDisk.ContainsKey($_) } | Sort-Object -CaseSensitive)
    if ($absent.Count -gt 0) {
        throw "Manifest lists $($absent.Count) file(s) that are missing from '$Root': $(($absent | Select-Object -First 10) -join ', ')."
    }

    foreach ($relative in ($expected.Keys | Sort-Object -CaseSensitive)) {
        $entry = $expected[$relative]
        $full = $onDisk[$relative]
        $actualSize = (Get-Item -LiteralPath $full).Length
        if ([int64]$entry.size -ne $actualSize) {
            throw "Manifest size for '$relative' is $($entry.size) but the file is $actualSize bytes."
        }
        $actualHash = Get-BundleFileSha256 -Path $full
        if ([string]$entry.sha256 -ne $actualHash) {
            throw "Manifest SHA-256 for '$relative' is $($entry.sha256) but the file hashes to $actualHash."
        }
    }
}

function Test-BundleManifestSchema {
    <#
        .SYNOPSIS
            Structural validation of a bundle manifest.
    #>
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Rid
    )

    $required = @(
        'schemaVersion', 'languageServerVersion', 'languageServerCommit', 'toolingVersion',
        'toolingCommit', 'compilerCompatibilityLine', 'rid', 'targetFramework',
        'runtimeVersion', 'publishMode', 'entrypoint', 'files'
    )
    foreach ($name in $required) {
        if (-not (Test-BundleManifestFieldPresent -Source $Manifest -Name $name)) {
            throw "bundle-manifest.json is missing required field '$name'."
        }
    }

    if ([int](Get-BundleManifestField -Source $Manifest -Name 'schemaVersion') -ne 2) {
        throw "bundle-manifest.json schemaVersion is '$($Manifest.schemaVersion)', expected 2."
    }
    if ((Get-BundleManifestField -Source $Manifest -Name 'rid') -ne $Rid) {
        throw "bundle-manifest.json rid is '$($Manifest.rid)' but the bundle was built for '$Rid'."
    }
    if ((Get-BundleManifestField -Source $Manifest -Name 'languageServerCommit') -notmatch $script:BundleSha256) {
        throw "bundle-manifest.json languageServerCommit '$($Manifest.languageServerCommit)' is not a full 40-character commit SHA."
    }
    if ((Get-BundleManifestField -Source $Manifest -Name 'entrypoint') -ne (Get-BundleEntrypointName -Rid $Rid)) {
        throw "bundle-manifest.json entrypoint '$($Manifest.entrypoint)' is not the expected entrypoint for '$Rid'."
    }

    $files = @($Manifest.files)
    if ($files.Count -eq 0) { throw 'bundle-manifest.json lists no files.' }
    $previous = $null
    foreach ($file in $files) {
        $relative = [string]$file.path
        if ($relative.Contains('\')) { throw "bundle-manifest.json path '$relative' is not /-separated." }
        if ($null -ne $previous -and [string]::CompareOrdinal($relative, $previous) -le 0) {
            throw "bundle-manifest.json file list is not byte-wise sorted at '$relative'."
        }
        $previous = $relative
    }

    $entrypoint = [string]$Manifest.entrypoint
    $entryFile = @($files | Where-Object { [string]$_.path -eq $entrypoint })
    if ($entryFile.Count -ne 1) { throw "bundle-manifest.json must list the entrypoint '$entrypoint' exactly once." }
    if (-not [bool]$entryFile[0].executable) { throw "bundle-manifest.json does not mark '$entrypoint' as the executable." }
}
