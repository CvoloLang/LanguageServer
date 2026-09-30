# Release bundle contract

Every LanguageServer release is published as five platform archives. This document
is the contract they all satisfy.

## Archive layout

Each archive is **flat**: every payload file sits at the archive root, with no
wrapping directory, identically on all five platforms.

```
cvolo-language-server-<version>-<rid>.zip        (win-x64)
cvolo-language-server-<version>-<rid>.tar.gz     (all other RIDs)
cvolo-language-server-<version>-<rid>.manifest.sha256
```

```
<RID>  in  win-x64  linux-x64  linux-arm64  osx-x64  osx-arm64
```

Extracted contents:

```
cvolo-language-server[.exe]     the entrypoint: one self-contained single-file build
bundle-manifest.json            the manifest described below
<the complete pinned tooling bundle, verbatim, 54 files for 0.0.21.0>
  tooling.manifest.json
  SHA256SUMS.txt
  Cvolo.Compiler.Tooling.dll     the only compile-time dependency
  Cvolo.{Analysis,Core,Core.Packages,Emitter.LLVM,Packaging,Projects,Syntax,Syntax.Antlr}.dll
  LLVMSharp.dll, LLVMSharp.Interop.dll
  K4os.Compression.LZ4{,.Streams}.dll, K4os.Hash.xxHash.dll
  NSec.Cryptography.dll, Blake3.dll, Antlr4.Runtime.Standard.dll
  libraries/…                   the Cvolo standard library (.cvl sources)
  docs/…                        CHANGELOG.md, README.md
```

**The bundle is a single executable entry point, not a single file.** The executable
embeds the .NET runtime and the server assemblies; the pinned tooling bundle is copied
beside it unchanged, because the server loads `Cvolo.Compiler.Tooling.dll` from its
application base directory at runtime. Deleting the side files produces a server that
starts and then cannot compile anything, which is why the payload is mandatory and
verified rather than optional.

The pinned `0.0.21.0` bundle is managed assemblies, standard-library sources and
docs. It contains **no compiler executable**, and the server never launches one:
diagnostics, completion, hover, navigation, rename, semantic tokens, type hierarchy,
type layout and code actions are all served in-process through `Compiler.Tooling`.
A consumer must not treat this archive as containing a separately invocable
compiler.

The payload is copied verbatim rather than curated, so its contents are whatever the
pinned tooling version ships. That is precisely why it is verified by hash against
the pinned `SHA256SUMS.txt` at package time instead of being pruned to a known file
list.

## bundle-manifest.json

`schemaVersion: 2`. Field order is fixed and the file list is byte-wise (ordinal)
sorted, so the same inputs always produce a byte-identical manifest.

```json
{
  "schemaVersion": 2,
  "languageServerVersion": "0.1.0-alpha.10",
  "languageServerCommit": "<40-character SHA>",
  "toolingVersion": "0.0.21.0",
  "toolingCommit": "<40-character SHA>",
  "compilerCompatibilityLine": "0.0.21",
  "rid": "win-x64",
  "targetFramework": "net10.0",
  "runtimeVersion": "10.0.12",
  "publishMode": "self-contained-single-file-with-tooling-payload",
  "entrypoint": "cvolo-language-server.exe",
  "files": [
    { "path": "…", "size": 0, "sha256": "…", "executable": false }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `languageServerVersion` | Product version, copied from `<Version>`. |
| `languageServerCommit` | The commit actually built. Release CI requires it to equal both `git rev-parse HEAD` and the peeled release tag, and requires a full 40-character SHA. |
| `toolingVersion` | Pinned tooling revision, copied from `tooling.version`. |
| `toolingCommit` | The compiler commit that tooling bundle was built from. |
| `compilerCompatibilityLine` | Compiler line, copied from `compiler-compatibility.version`. |
| `runtimeVersion` | The bundled .NET runtime patch version, or `null` when the build does not report one. |

Every identity field is read **from the executable being packaged**, not from the
pipeline's own variables, and is then cross-checked against the values the pipeline
independently derived. The manifest therefore cannot describe a different build than
the one in the archive. No commit SHA is hardcoded anywhere in the scripts.

## Deterministic staging

The pipeline never searches `bin/`, `obj/` or any previous `artifacts/` tree for an
executable, and never picks "the newest" or "first matching" file. A stale
RID-specific `bin/Release/net10.0/win-x64/cvolo-language-server.exe` is a permanent
fixture of a developer workspace, and a tree assembled by copying one publish over
another previously produced a nested `win-x64/` and a stray sibling `osx-arm64/`.

The rules:

1. Every publish and packaging step owns an explicit output directory, created fresh
   (`rm -rf` then `mkdir -p`) immediately before publishing into it.
2. `new-bundle-manifest.ps1`, `pack-release-asset.ps1` and `test-release-asset.ps1`
   all take the publish or archive path as a mandatory parameter. None of them
   enumerates anything to discover it.
3. `test-release-asset.ps1` runs the executable **extracted from the archive under
   test**, never one found in a project `bin/` tree.
4. The staging tree is rejected when it contains a directory named after any known
   RID, or a file that is neither the entrypoint nor part of the pinned tooling
   bundle, or a tooling file that no longer matches the pinned bundle's
   `SHA256SUMS.txt`.

Rule 4 is what makes a stale file impossible to ship. Checking against the pinned
bundle rather than by re-listing the staging directory is deliberate: a manifest
generated by enumerating the directory would describe any stale file it found and
could never fail.

## Pipeline

`validate` -> `test` (ubuntu + windows) -> `publish` (5 RIDs) -> `release`.

`validate` refuses the release unless all of the following hold:

- the tag is `v<semver>` and matches `<Version>` in `Directory.Build.props`;
- the version is a `0.x.y…` LanguageServer series version;
- `git rev-parse HEAD` equals `git rev-parse "<tag>^{commit}"`;
- that commit is a full 40-character SHA.

`publish` then, per RID: provisions the pinned tooling through the compatibility gate,
restores locked, publishes into a fresh directory with
`-p:LanguageServerCommit=<validated commit>`, generates the manifest, packages, and
verifies the packaged archive.

## Published assets

The GitHub Release contains exactly eleven files:

- 5 platform archives
- 5 `<archive-name>.manifest.sha256` sidecars, each covering `bundle-manifest.json`
- `SHA256SUMS`

`SHA256SUMS` covers **all ten** per-platform files, in a fixed order, one
`<sha256>  <name>` line each. It is generated by the `release` job from the exact
expected filenames rather than from a directory glob, and the job additionally
extracts each archive's `bundle-manifest.json` and verifies it against its sidecar
before the release is created. The `release` job fails if the archive count, the
sidecar count or the total published file count is not exactly 5, 5 and 11.
