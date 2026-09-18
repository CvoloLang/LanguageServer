# Cvolo.LanguageServer release pipeline patch

Drop these files into the repository root.

This patch is based on the supplied LSP-2 repository snapshot:
- server project: `src/Cvolo.LanguageServer/Cvolo.LanguageServer.csproj`
- solution: `src/Cvolo.LanguageServer.slnx`
- tests: `tests/Cvolo.LanguageServer.Tests/Cvolo.LanguageServer.Tests.csproj`
- Tooling bootstrap: `build/fetch-tooling.ps1` / `.sh`
- version: `Directory.Build.props`

## Trigger

Set `Directory.Build.props` to the release version, for example:

```xml
<Version>0.1.0-alpha.1</Version>
```

Commit, then create/push:

```text
v0.1.0-alpha.1
```

`release.yml` requires the tag without its leading `v` to equal `<Version>`.

## Produced assets

- `cvolo-language-server-<version>-win-x64.zip`
- `cvolo-language-server-<version>-linux-x64.tar.gz`
- `cvolo-language-server-<version>-linux-arm64.tar.gz`
- `cvolo-language-server-<version>-osx-x64.tar.gz`
- `cvolo-language-server-<version>-osx-arm64.tar.gz`
- per-RID `*.manifest.sha256`
- `SHA256SUMS`

The GitHub release is marked prerelease.

## Publish mode

The workflow deliberately uses:

```text
--self-contained true
PublishSingleFile=true
PublishTrimmed=false
PublishAot=false
```

Native AOT is intentionally not enabled yet. It should be a separate compatibility increment because the current Language Server dynamically consumes the pinned compiler Tooling bundle and the supplied snapshot has not established Native AOT compatibility.

## Important blocker before calling this VSCode-1-compliant

The VSCode-1 v3 design requires `runtimeVersion` in the producer manifest and release-set consistency across all RIDs. The current supplied Language Server snapshot exposes server/tooling/compiler-line via `--version`, but does not provide a stable embedded .NET runtime patch value to the release script.

Therefore `new-bundle-manifest.ps1` writes:

```json
"runtimeVersion": null
```

on purpose rather than inventing it.

Before using these artifacts as final VSCode-1 production inputs, add a reliable build-time runtime-version source and make the script require it.

## Runner note

The workflow uses GitHub-hosted native architecture runners:
- Windows x64
- Ubuntu x64
- Ubuntu arm64
- macOS Intel
- macOS arm64

Runner labels can evolve. If GitHub changes availability for your repository/plan, adjust only the `matrix.os` labels; RID mapping remains unchanged.

## Existing CI

Keep the existing `.github/workflows/ci.yml`. This archive adds a separate tag-only `release.yml`.
