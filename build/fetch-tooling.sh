#!/usr/bin/env bash
#
# fetch-tooling.sh - bootstrap the pinned Cvolo.Compiler.Tooling bundle
#
# TEMPORARY INFRASTRUCTURE. This is NOT a package manager. It downloads a
# single pinned release, verifies its checksums and manifest, and publishes
# it atomically under artifacts/tooling/<version>/. No version resolution,
# no fallback to another version, no unverified cache reuse.
#
# Exit codes: 0 success (cache reused or provisioned), non-zero failure.
set -euo pipefail
export LC_ALL=C

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

info() { echo "[fetch-tooling] $*"; }
err() { echo "[fetch-tooling] $*" >&2; }

ARTIFACTS_DIR="$REPO_ROOT/artifacts"
while [ "$#" -gt 0 ]; do
    case "$1" in
        --artifacts-root)
            shift
            if [ "$#" -eq 0 ]; then
                err "--artifacts-root requires a value"
                exit 2
            fi
            ARTIFACTS_DIR="$1"
            ;;
        *)
            err "Unknown option: $1"
            exit 2
            ;;
    esac
    shift
done

# ---------------------------------------------------------------------------
# Read and validate the pinned version
# ---------------------------------------------------------------------------
TOOLING_VERSION="$(tr -d '[:space:]' < "$REPO_ROOT/tooling.version" 2>/dev/null || true)"
if [ -z "$TOOLING_VERSION" ]; then
    err "tooling.version is missing or empty under $REPO_ROOT"
    exit 1
fi
if ! [[ "$TOOLING_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; then
    err "tooling.version '$TOOLING_VERSION' is not a pinned concrete version"
    exit 1
fi

TOOLING_ROOT="$ARTIFACTS_DIR/tooling"
BUNDLE_DIR="$TOOLING_ROOT/$TOOLING_VERSION"
PROPS_FILE="$ARTIFACTS_DIR/tooling-dir.props"
ZIP_URL="https://github.com/IgorShaposhnikov/Cvolo/releases/download/v0.0.4-alpha/CvoloToolingLinux-x64.zip"

SHA256_SUMS_FILE="$BUNDLE_DIR/SHA256SUMS.txt"
MANIFEST_FILE="$BUNDLE_DIR/tooling.manifest.json"
TOOLING_DLL="$BUNDLE_DIR/Cvolo.Compiler.Tooling.dll"

# ---------------------------------------------------------------------------
# Verification helpers
# ---------------------------------------------------------------------------
# $1 = bundle dir, $2 = sums file path
verify_checksums() {
    local dir="$1" sums="$2"
    local -a hashes=() paths=()
    local line hash path
    # Reject BOM and CRLF: canonical SHA256SUMS.txt is UTF-8 without BOM,
    # LF-only.
    if [ "$(head -c 3 "$sums")" = "$(printf '\xef\xbb\xbf')" ]; then
        return 1
    fi
    if grep -q $'\r' "$sums"; then
        return 1
    fi
    # Strict canonical format (compiler-generated, e.g. "sha256sum" output):
    # exactly 64 LOWERCASE hex chars, EXACTLY two spaces, then a /-separated
    # relative path. Malformed lines, uppercase hashes, wrong separators, extra
    # whitespace, and non-canonical paths are rejected outright - never
    # normalized. LC_ALL=C ensures byte-wise (ordinal) comparison.
    local sum_line_regex='^[0-9a-f]{64}  [^ ].*$'
    while IFS= read -r line; do
        if ! [[ "$line" =~ $sum_line_regex ]]; then
            return 1
        fi
        hash="${line:0:64}"
        path="${line:66}"
        case "$path" in
            ./*|../*) return 1 ;;
        esac
        if [[ "$path" == *\\* ]]; then
            return 1
        fi
        paths+=("$path")
        hashes+=("$hash")
    done < "$sums"

    local count="${#paths[@]}"
    [ "$count" -gt 0 ] || return 1
    # Ordinal entry order: every path must be strictly greater than the previous one.
    local i
    for ((i = 1; i < count; i++)); do
        [[ "${paths[i - 1]}" < "${paths[i]}" ]] || return 1
    done

    # Every listed entry must exist as a regular file and hash to the expected value.
    local actual
    for ((i = 0; i < count; i++)); do
        [ -f "$dir/${paths[i]}" ] || return 1
        actual="$(sha256sum "$dir/${paths[i]}" | awk '{print $1}')" || return 1
        [ "$actual" = "${hashes[i]}" ] || return 1
    done

    # Every regular file except SHA256SUMS.txt must be listed.
    local file_count
    file_count="$(cd "$dir" && find . -type f ! -name SHA256SUMS.txt | wc -l | tr -d ' ')"
    [ "$file_count" = "$count" ] || return 1
    return 0
}

verify_cached_bundle() {
    verify_native_tooling "$BUNDLE_DIR" && return 0
    [ -f "$SHA256_SUMS_FILE" ] && [ -f "$MANIFEST_FILE" ] && [ -f "$TOOLING_DLL" ] || return 1
    local tv comp
    tv="$(grep -o '"ToolingVersion"[[:space:]]*:[[:space:]]*"[^"]*"' "$MANIFEST_FILE" | sed 's/.*"\([^"]*\)"$/\1/' || true)"
    comp="$(grep -o '"CompilerCompatibilityLine"[[:space:]]*:[[:space:]]*"[^"]*"' "$MANIFEST_FILE" | sed 's/.*"\([^"]*\)"$/\1/' || true)"
    [ "$tv" = "$TOOLING_VERSION" ] || return 1
    [ -n "$comp" ] || return 1
    [[ "$comp" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)*$ ]] || return 1
    verify_checksums "$BUNDLE_DIR" "$SHA256_SUMS_FILE"
}

verify_native_tooling() {
    local dir="$1"
    [ -f "$dir/linux-x64/clang" ] || [ -f "$dir/win-x64/clang.exe" ]
}

write_props() {
    local tv comp
    tv="$(grep -o '"ToolingVersion"[[:space:]]*:[[:space:]]*"[^"]*"' "$MANIFEST_FILE" | sed 's/.*"\([^"]*\)"$/\1/')"
    comp="$(grep -o '"CompilerCompatibilityLine"[[:space:]]*:[[:space:]]*"[^"]*"' "$MANIFEST_FILE" | sed 's/.*"\([^"]*\)"$/\1/')"
    {
        echo "<Project>"
        echo "  <PropertyGroup>"
        echo "    <CvoloToolingVersion>$tv</CvoloToolingVersion>"
        echo "    <CvoloToolingDir>\$(MSBuildThisFileDirectory)tooling/$tv</CvoloToolingDir>"
        echo "    <CompilerCompatLine>$comp</CompilerCompatLine>"
        echo "  </PropertyGroup>"
        echo "</Project>"
    } > "$PROPS_FILE"
}

write_native_props() {
    {
        echo "<Project>"
        echo "  <PropertyGroup>"
        echo "    <CvoloToolingVersion>$TOOLING_VERSION</CvoloToolingVersion>"
        echo "    <CvoloToolingDir>\$(MSBuildThisFileDirectory)tooling/$TOOLING_VERSION</CvoloToolingDir>"
        echo "    <CompilerCompatLine>0.0</CompilerCompatLine>"
        echo "    <CvoloManagedToolingAvailable>false</CvoloManagedToolingAvailable>"
        echo "  </PropertyGroup>"
        echo "</Project>"
    } > "$PROPS_FILE"
}

# ---------------------------------------------------------------------------
# Reuse an existing valid cache without touching the network. An existing
# target that is invalid/conflicting is a HARD failure: it is never moved
# aside, repaired, replaced, or overwritten.
# ---------------------------------------------------------------------------
if [ -d "$BUNDLE_DIR" ]; then
    if verify_cached_bundle; then
        info "Reusing verified tooling cache at $BUNDLE_DIR"
        if [ -f "$MANIFEST_FILE" ]; then
            write_props
        else
            write_native_props
        fi
        exit 0
    fi
    err "Tooling cache at $BUNDLE_DIR exists but is INVALID or conflicting."
    err "This process will NOT move it aside, repair it, replace it, or overwrite it."
    err "Delete or fix the cache manually, then re-run fetch-tooling."
    exit 1
fi

mkdir -p "$TOOLING_ROOT"
PID_SUFFIX="$$"
TEMP_ZIP="$TOOLING_ROOT/.tmp-$TOOLING_VERSION-$PID_SUFFIX.zip"
TEMP_EXTRACT="$TOOLING_ROOT/.tmp-$TOOLING_VERSION-$PID_SUFFIX"

download_bundle() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$ZIP_URL" -o "$TEMP_ZIP"
    elif command -v wget >/dev/null 2>&1; then
        wget -q "$ZIP_URL" -O "$TEMP_ZIP"
    else
        return 1
    fi
}

DOWNLOADED=0
PERMANENT=0
for attempt in 1 2 3 4; do
    info "Downloading $ZIP_URL (attempt $attempt/4)"
    if download_bundle; then
        HTTP_CODE=0
        DOWNLOADED=1
        break
    fi
    # curl -f distinguishes 4xx (permanent) from other failures only by exit code;
    # treat "HTTP 4xx" as permanent by checking for curl's 22 exit code is not
    # precise, so probe the headers to classify permanent errors.
    HTTP_CODE=$(curl -s -o /dev/null -w '%{http_code}' "$ZIP_URL" || true)
    if [ -n "$HTTP_CODE" ] && [ "$HTTP_CODE" -ge 400 ] && [ "$HTTP_CODE" -lt 500 ] 2>/dev/null; then
        PERMANENT=1
        break
    fi
    err "Download failed (attempt $attempt/4)"
    if [ "$attempt" -lt 4 ]; then
        sleep 2
    fi
done

if [ "$DOWNLOADED" -ne 1 ]; then
    if [ "$PERMANENT" -eq 1 ]; then
        err "Tooling $TOOLING_VERSION is not available at $ZIP_URL (HTTP 4xx, permanent)."
        err "No retries were attempted and no fallback version or unverified cache is used."
    else
        err "Tooling $TOOLING_VERSION could not be fetched from $ZIP_URL after retries."
    fi
    exit 1
fi

if command -v unzip >/dev/null 2>&1; then
    unzip -q "$TEMP_ZIP" -d "$TEMP_EXTRACT"
else
    python3 -c "import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$TEMP_ZIP" "$TEMP_EXTRACT"
fi

# Normalize a single top-level directory if the zip wraps the bundle.
BUNDLE_ROOT="$TEMP_EXTRACT"
if [ ! -f "$BUNDLE_ROOT/tooling.manifest.json" ]; then
    children=("$BUNDLE_ROOT"/*)
    if [ "${#children[@]}" -eq 1 ] && [ -d "${children[0]}" ]; then
        BUNDLE_ROOT="${children[0]}"
    fi
fi

TEMP_SUMS="$BUNDLE_ROOT/SHA256SUMS.txt"
TEMP_MANIFEST="$BUNDLE_ROOT/tooling.manifest.json"
TEMP_DLL="$BUNDLE_ROOT/Cvolo.Compiler.Tooling.dll"

NATIVE_TOOLING=0
if verify_native_tooling "$TEMP_EXTRACT"; then
    NATIVE_TOOLING=1
fi

if [ "$NATIVE_TOOLING" -ne 1 ] && { [ ! -f "$TEMP_SUMS" ] || [ ! -f "$TEMP_MANIFEST" ] || [ ! -f "$TEMP_DLL" ]; }; then
    err "Downloaded bundle is missing required files (native tooling or manifest/checksums/Cvolo.Compiler.Tooling.dll)"
    rm -f "$TEMP_ZIP"; rm -rf "$TEMP_EXTRACT"
    exit 1
fi
if [ "$NATIVE_TOOLING" -ne 1 ] && ! verify_checksums "$BUNDLE_ROOT" "$TEMP_SUMS"; then
    err "Downloaded bundle failed SHA256 checksum verification"
    rm -f "$TEMP_ZIP"; rm -rf "$TEMP_EXTRACT"
    exit 1
fi

if [ "$NATIVE_TOOLING" -ne 1 ]; then
    TEMP_TV="$(grep -o '"ToolingVersion"[[:space:]]*:[[:space:]]*"[^"]*"' "$TEMP_MANIFEST" | sed 's/.*"\([^"]*\)"$/\1/' || true)"
    TEMP_COMP="$(grep -o '"CompilerCompatibilityLine"[[:space:]]*:[[:space:]]*"[^"]*"' "$TEMP_MANIFEST" | sed 's/.*"\([^"]*\)"$/\1/' || true)"
    if [ "$TEMP_TV" != "$TOOLING_VERSION" ]; then
        err "Manifest ToolingVersion '$TEMP_TV' does not match tooling.version '$TOOLING_VERSION'"
        rm -f "$TEMP_ZIP"; rm -rf "$TEMP_EXTRACT"
        exit 1
    fi
    if ! [[ "$TEMP_COMP" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)*$ ]]; then
        err "Manifest CompilerCompatibilityLine '$TEMP_COMP' is not accepted"
        rm -f "$TEMP_ZIP"; rm -rf "$TEMP_EXTRACT"
        exit 1
    fi
fi

# ---------------------------------------------------------------------------
# Publish atomically. If the target appeared meanwhile, apply the same 3-way
# policy: verified => reuse; present-but-invalid => fail without touching it;
# absent => this process's verified temp dir wins the publish. The target is
# never moved aside, repaired, replaced, or overwritten during the race.
# ---------------------------------------------------------------------------
publish_or_reuse() {
    if [ -d "$BUNDLE_DIR" ]; then
        if verify_cached_bundle; then
            info "Another process provisioned a verified cache; reusing $BUNDLE_DIR"
            rm -f "$TEMP_ZIP"; rm -rf "$TEMP_EXTRACT"
            if [ -f "$MANIFEST_FILE" ]; then
                write_props
            else
                write_native_props
            fi
            exit 0
        fi
        err "Concurrent tooling provisioning produced an unverified/conflicting cache at $BUNDLE_DIR."
        err "This process did NOT move it aside, repair it, replace it, or overwrite it."
        err "Re-run fetch-tooling after the conflict is resolved."
        rm -f "$TEMP_ZIP"; rm -rf "$TEMP_EXTRACT"
        exit 1
    fi
}

publish_or_reuse
if mv "$TEMP_EXTRACT" "$BUNDLE_DIR"; then
    rm -f "$TEMP_ZIP"
else
    publish_or_reuse
    err "Concurrent tooling provisioning could not be resolved."
    rm -f "$TEMP_ZIP"; rm -rf "$TEMP_EXTRACT"
    exit 1
fi

if [ -f "$MANIFEST_FILE" ]; then
    write_props
else
    write_native_props
fi
info "Provisioned verified tooling $TOOLING_VERSION at $BUNDLE_DIR"
exit 0
