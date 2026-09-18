# Converts an absolute local filesystem path into an absolute file: URI.
#
# This must work identically on Windows, Linux and macOS. `new Uri($path)` and
# `new Uri($path, [UriKind]::Absolute)` only understand Windows drive paths and
# throw UriFormatException for POSIX rooted paths (e.g. "/tmp/x"), which made the
# release smoke send a null workspace/document URI on Linux and macOS. UriBuilder
# with the file scheme is platform-neutral and escapes URI-sensitive characters.

function ConvertTo-FileUri {
    [CmdletBinding()]
    [OutputType([System.Uri])]
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Cannot construct a file URI from an empty path.'
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)

    # Normalize Windows separators and guarantee a rooted path so UriBuilder
    # emits three slashes ("file:///C:/..." / "file:///tmp/...").
    $normalized = $fullPath.Replace('\', '/')
    if (-not $normalized.StartsWith('/')) {
        $normalized = '/' + $normalized
    }

    $builder = [System.UriBuilder]::new()
    $builder.Scheme = [System.Uri]::UriSchemeFile
    $builder.Host = ''
    $builder.Path = $normalized

    $uri = $builder.Uri
    if ($null -eq $uri -or -not $uri.IsAbsoluteUri -or $uri.Scheme -ne [System.Uri]::UriSchemeFile) {
        throw "Failed to construct an absolute file URI from '$Path' (result: '$uri')."
    }

    return $uri
}
