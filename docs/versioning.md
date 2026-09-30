# Versioning

Every Cvolo component carries its own product version. Nothing in this repository
derives the LanguageServer product version from the compiler or the tooling.

## The three identities

| Identity | Source of truth | Current value | Meaning |
| --- | --- | --- | --- |
| `languageServerVersion` | `Directory.Build.props` -> `<Version>` | `0.1.0-alpha.9` | The LanguageServer product version. This is what the tag, the release and the VS Code extension refer to. |
| `toolingVersion` | `tooling.version` | `0.0.21.0` | The exact pinned `Cvolo.Compiler.Tooling` revision this release is built against. |
| `compilerCompatibilityLine` | `compiler-compatibility.version` | `0.0.21` | The compiler line this LanguageServer release supports. |

`toolingVersion` and `compilerCompatibilityLine` are **compatibility metadata**, not
product identity. They change when the compiler moves; the product version changes
when the LanguageServer changes.

`build/fetch-tooling.ps1` and `build/fetch-tooling.sh` hard-fail unless
`compiler-compatibility.version` equals the `CompilerCompatibilityLine` in the pinned
`tooling.manifest.json`, and unless `tooling.version` equals the `ToolingVersion` in
that manifest. The same gate is re-applied to the files actually shipped inside a
release archive, so a bundle can never carry a compiler line the tooling disagrees
with.

## Progression

The LanguageServer product series is independent and monotonically increasing:

```
0.1.0-alpha.8   historical
0.1.0-alpha.9   <- current
0.1.0-alpha.10
...
0.1.0-beta.0
0.1.0-beta.1
...
0.1.0-rc.0
...
0.1.0
then 0.1.1 (patch scope) or 0.2.0 (breaking product change)
```

Increment rules:

- **alpha / beta / rc revision** -- anything that is not a committed SemVer boundary:
  new LSP features, new protocol handlers, bug fixes, tooling bumps, toolchain bumps.
- **patch (`0.1.1`)** -- fixes and internal changes that add no protocol surface and
  break no client.
- **minor (`0.2.0`)** -- a deliberate LSP product step: changed defaults, removed
  capabilities, new required client behaviour.
- **major (`1.0.0`)** -- reserved for a stable, compatibility-guaranteed release.

An alpha revision is consumed for **any** release-worthy change, including a pure
metadata change such as a tooling bump:

```
Compiler line              0.0.21    unchanged
Tooling                    0.0.21.0 -> 0.0.21.1
LSP                        0.1.0-alpha.9 -> 0.1.0-alpha.10

Compiler moves to 0.0.22
Tooling                    0.0.22.0
LSP                        0.1.0-alpha.11   (the LSP product itself did not change)
```

Moving the compiler to a new line does **not** create a new LSP product series. It
changes `compiler-compatibility.version` and `tooling.version`, and therefore consumes
an LSP alpha revision.

A version is never reused and a tag is never moved. If a release turns out to be
wrong, the fix is published under the next version.

## Branches

- `master` -- the latest successfully released LanguageServer commit.
- `develop/0.1.0` -- active development of the `0.1.0` prerelease series.
- `feature/*` -- short-lived, branched from `develop/0.1.0`.

The development branch is named for the **LanguageServer product series**, not for
the compiler line. A compiler-line change does not rename the branch.

## Reporting

```
$ cvolo-language-server --version
Cvolo Language Server 0.1.0-alpha.9
Commit: 0000000000000000000000000000000000000000
Tooling: 0.0.21.0
Compiler compatibility: 0.0.21

$ cvolo-language-server --version --json
{"languageServerVersion":"0.1.0-alpha.9","languageServerCommit":"...","toolingVersion":"0.0.21.0","toolingCommit":"...","compilerCompatibilityLine":"0.0.21","rid":"win-x64","targetFramework":"net10.0","runtimeVersion":"10.0.12"}
```

`--version --json` is the machine-readable form and uses exactly the field names of
`bundle-manifest.json`, so a consumer can compare the two without translation.
`--json` without `--version` is rejected with a non-zero exit code. `--verbose` is
logging-only and has no effect on `--version` output.

The product version is the assembly `InformationalVersion` and is a clean SemVer.
`IncludeSourceRevisionInInformationalVersion` is disabled, because provenance belongs
in `languageServerCommit`, not in the product version string.
