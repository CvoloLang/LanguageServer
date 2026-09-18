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
ZIP_URL="https://github.com/IgorShaposhnikov/Cvolo/releases/download/tooling-$TOOLING_VERSION/CvoloLanguageServerTooling-$TOOLING_VERSION.zip"
ZIP_SHA_URL="$ZIP_URL.sha256"

SHA256_SUMS_FILE="$BUNDLE_DIR/SHA256SUMS.txt"
MANIFEST_FILE="$BUNDLE_DIR/tooling.manifest.json"
TOOLING_DLL="$BUNDLE_DIR/Cvolo.Compiler.Tooling.dll"

# ---------------------------------------------------------------------------
# Verification helpers
# ---------------------------------------------------------------------------
# $1 = file path. Portable SHA-256: GNU coreutils (Linux) provides sha256sum,
# macOS ships shasum, and minimal images may only have openssl.
sha256_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        openssl dgst -sha256 "$1" | awk '{print $NF}'
    fi
}

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
        actual="$(sha256_file "$dir/${paths[i]}")" || return 1
        [ "$actual" = "${hashes[i]}" ] || return 1
    done

    # Every regular file except SHA256SUMS.txt must be listed.
    local file_count
    file_count="$(cd "$dir" && find . -type f ! -name SHA256SUMS.txt | wc -l | tr -d ' ')"
    [ "$file_count" = "$count" ] || return 1
    return 0
}

manifest_string_field() {
    # $1 = manifest file, $2 = field name
    grep -o "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" "$1" | sed 's/.*"\([^"]*\)"$/\1/' || true
}

manifest_is_null_field() {
    # $1 = manifest file, $2 = field name; true only if present and explicitly null
    grep -Eq "\"$2\"[[:space:]]*:[[:space:]]*null([[:space:]]*[,}])" "$1"
}

# The producer manifest contract: version identity, compiler compatibility line,
# the compiler revision it was built from, its target framework, RID-neutrality
# (RuntimeIdentifier must be null), and an immutable source commit.
verify_manifest_fields() {
    local manifest="$1" tv comp built tf commit
    tv="$(manifest_string_field "$manifest" ToolingVersion)"
    comp="$(manifest_string_field "$manifest" CompilerCompatibilityLine)"
    built="$(manifest_string_field "$manifest" BuiltFromCompilerVersion)"
    tf="$(manifest_string_field "$manifest" TargetFramework)"
    commit="$(manifest_string_field "$manifest" Commit)"
    [ "$tv" = "$TOOLING_VERSION" ] || return 1
    [ -n "$comp" ] || return 1
    [[ "$comp" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)*$ ]] || return 1
    [ -n "$built" ] || return 1
    [ -n "$tf" ] || return 1
    manifest_is_null_field "$manifest" RuntimeIdentifier || return 1
    [ -n "$commit" ] || return 1
    return 0
}

verify_cached_bundle() {
    [ -f "$SHA256_SUMS_FILE" ] && [ -f "$MANIFEST_FILE" ] && [ -f "$TOOLING_DLL" ] || return 1
    verify_manifest_fields "$MANIFEST_FILE" || return 1
    verify_checksums "$BUNDLE_DIR" "$SHA256_SUMS_FILE"
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

# ---------------------------------------------------------------------------
# Reuse an existing valid cache without touching the network. An existing
# target that is invalid/conflicting is a HARD failure: it is never moved
# aside, repaired, replaced, or overwritten.
# ---------------------------------------------------------------------------
if [ -d "$BUNDLE_DIR" ]; then
    if verify_cached_bundle; then
        info "Reusing verified tooling cache at $BUNDLE_DIR"
        write_props
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
TEMP_SHA="$TOOLING_ROOT/.tmp-$TOOLING_VERSION-$PID_SUFFIX.zip.sha256"
TEMP_EXTRACT="$TOOLING_ROOT/.tmp-$TOOLING_VERSION-$PID_SUFFIX"

download_file() {
    local url="$1" dest="$2"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$url" -o "$dest"
    elif command -v wget >/dev/null 2>&1; then
        wget -q "$url" -O "$dest"
    else
        return 1
    fi
}

download_with_retries() {
    # $1 = url, $2 = dest, $3 = label
    local url="$1" dest="$2" label="$3" attempt http_code
    for attempt in 1 2 3 4; do
        info "Downloading $label (attempt $attempt/4): $url"
        if download_file "$url" "$dest"; then
            return 0
        fi
        # curl -f distinguishes 4xx (permanent) from other failures only by exit
        # code; probe the headers to classify permanent errors.
        http_code=$(curl -s -o /dev/null -w '%{http_code}' "$url" || true)
        if [ -n "$http_code" ] && [ "$http_code" -ge 400 ] && [ "$http_code" -lt 500 ] 2>/dev/null; then
            err "Tooling $TOOLING_VERSION is not available at $url (HTTP 4xx, permanent)."
            err "No retries were attempted and no fallback version or unverified cache is used."
            return 1
        fi
        err "Download failed (attempt $attempt/4)"
        if [ "$attempt" -lt 4 ]; then
            sleep 2
        fi
    done
    return 1
}

if ! download_with_retries "$ZIP_URL" "$TEMP_ZIP" "tooling archive"; then
    err "Tooling $TOOLING_VERSION could not be fetched from $ZIP_URL."
    exit 1
fi

# Verify the downloaded archive against the published .sha256 sidecar asset
# BEFORE it is extracted or used. No unverified fallback is permitted.
if ! download_with_retries "$ZIP_SHA_URL" "$TEMP_SHA" "tooling archive checksum"; then
    rm -f "$TEMP_ZIP"
    exit 1
fi

EXPECTED_ZIP_HASH="$(grep -oiE '^[0-9a-f]{64}' "$TEMP_SHA" | head -n 1 | tr 'A-F' 'a-f')"
if ! [[ "$EXPECTED_ZIP_HASH" =~ ^[0-9a-f]{64}$ ]]; then
    err "Published checksum asset $ZIP_SHA_URL is malformed."
    rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"
    exit 1
fi
ACTUAL_ZIP_HASH="$(sha256_file "$TEMP_ZIP")"
if [ "$ACTUAL_ZIP_HASH" != "$EXPECTED_ZIP_HASH" ]; then
    err "Downloaded tooling archive does not match the published checksum asset."
    err "  expected: $EXPECTED_ZIP_HASH"
    err "  actual:   $ACTUAL_ZIP_HASH"
    rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"
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

if [ ! -f "$TEMP_SUMS" ] || [ ! -f "$TEMP_MANIFEST" ] || [ ! -f "$TEMP_DLL" ]; then
    err "Downloaded bundle is missing required files (manifest/checksums/Cvolo.Compiler.Tooling.dll)"
    rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"; rm -rf "$TEMP_EXTRACT"
    exit 1
fi
if ! verify_checksums "$BUNDLE_ROOT" "$TEMP_SUMS"; then
    err "Downloaded bundle failed SHA256 checksum verification"
    rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"; rm -rf "$TEMP_EXTRACT"
    exit 1
fi

if ! verify_manifest_fields "$TEMP_MANIFEST"; then
    err "Manifest does not satisfy the producer contract (ToolingVersion/CompilerCompatibilityLine/BuiltFromCompilerVersion/TargetFramework/RuntimeIdentifier/Commit)"
    rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"; rm -rf "$TEMP_EXTRACT"
    exit 1
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
            rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"; rm -rf "$TEMP_EXTRACT"
            write_props
            exit 0
        fi
        err "Concurrent tooling provisioning produced an unverified/conflicting cache at $BUNDLE_DIR."
        err "This process did NOT move it aside, repair it, replace it, or overwrite it."
        err "Re-run fetch-tooling after the conflict is resolved."
        rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"; rm -rf "$TEMP_EXTRACT"
        exit 1
    fi
}

publish_or_reuse
if mv "$TEMP_EXTRACT" "$BUNDLE_DIR"; then
    rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"
else
    publish_or_reuse
    err "Concurrent tooling provisioning could not be resolved."
    rm -f "$TEMP_ZIP"; rm -f "$TEMP_SHA"; rm -rf "$TEMP_EXTRACT"
    exit 1
fi

write_props
info "Provisioned verified tooling $TOOLING_VERSION at $BUNDLE_DIR"
exit 0
