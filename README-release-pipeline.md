# Cvolo.LanguageServer release pipeline patch

Drop these files into the repository root.

This patch is based on the supplied LSP-2 repository snapshot:
- server project: `src/Cvolo.LanguageServer/Cvolo.LanguageServer.csproj`
- solution: `src/Cvolo.LanguageServer.slnx`
- tests: `tests/Cvolo.LanguageServer.Tests/Cvolo.LanguageServer.Tests.csproj`
- Tooling bootstrap: `build/fetch-tooling.ps1` / `.sh`
- version: `Directory.Build.props`

## Trigger

Set `Directory.Build.props` to the release version. The LanguageServer product
version follows its own `0.1.0-alpha.N` series and is deliberately independent
of the compiler line, for example:

```xml
<Version>0.1.0-alpha.9</Version>
```

Keep `tooling.version` and `compiler-compatibility.version` in step with the
tooling bundle the release is built against. Those two files are compatibility
metadata, not the product version.

Commit, then create/push the matching tag:

```text
v0.1.0-alpha.9
```

`release.yml` requires all of the following, and fails the release otherwise:

- the tag without its leading `v` equals `<Version>` in `Directory.Build.props`;
- that version is a `0.x.y…` LanguageServer series version;
- `git rev-parse HEAD` equals `git rev-parse "<tag>^{commit}"`;
- that commit is a full 40-character SHA.

The validated commit is passed to the build as `-p:LanguageServerCommit=…` and is
embedded in the executable, so the shipped binary, the manifest and the tag all name
the same commit. No commit SHA is hardcoded in the pipeline.

## Produced assets

The GitHub release contains exactly eleven files:

- `cvolo-language-server-<version>-win-x64.zip`
- `cvolo-language-server-<version>-linux-x64.tar.gz`
- `cvolo-language-server-<version>-linux-arm64.tar.gz`
- `cvolo-language-server-<version>-osx-x64.tar.gz`
- `cvolo-language-server-<version>-osx-arm64.tar.gz`
- per-RID `*.manifest.sha256` (5), each covering that archive's `bundle-manifest.json`
- `SHA256SUMS`, covering **all ten** per-platform files above

The `release` job builds `SHA256SUMS` from the exact expected filenames rather than a
directory glob, asserts the archive, sidecar and total file counts, and verifies each
archive's `bundle-manifest.json` against its sidecar before creating the release.

The GitHub release is marked prerelease.

See `docs/bundle-payload.md` for the archive layout, the `schemaVersion: 2` manifest
and the deterministic staging rules.

## Publish mode

The workflow deliberately uses:

```text
--self-contained true
PublishSingleFile=true
PublishTrimmed=false
PublishAot=false
```

Native AOT is intentionally not enabled yet. It should be a separate compatibility increment because the current Language Server dynamically consumes the pinned compiler Tooling bundle and the supplied snapshot has not established Native AOT compatibility.

## Publish output is owned by the release

Each RID publishes into a fresh `artifacts/release/publish/<rid>` that is removed and
recreated immediately before the publish. No release step searches `bin/`, `obj/` or
any previous `artifacts/` tree for an executable, and `test-release-asset.ps1` runs
the executable extracted from the archive under test. A stale
`bin/Release/net10.0/win-x64/cvolo-language-server.exe` left in a developer workspace
therefore cannot be picked up, and a staging tree that still contains one fails the
build.

## runtimeVersion

`runtimeVersion` is populated from the bundled .NET runtime patch version of the
publishing runner and embedded in the executable and the manifest, so it is a real
measured value rather than an invented one. It is `null` only if a build genuinely
does not report one.

## Runner note

The workflow uses GitHub-hosted native architecture runners:
- Windows x64
- Ubuntu x64
- Ubuntu arm64
- macOS Intel
- macOS arm64

Runner labels can evolve. If GitHub changes availability for your repository/plan, adjust only the `matrix.os` labels; RID mapping remains unchanged.

## Ordinary CI

`.github/workflows/ci.yml` runs on pushes to `master` and to `develop/**` and on
pull requests, so the development branch is tested before anything is tagged.
`release.yml` is tag-driven only; it has no manual trigger. A transient failure is
retried with the GitHub Actions rerun button on the same tagged commit. If code must
change after a tag was created, the fix is released under the next version -- a tag
is never moved or reused.
