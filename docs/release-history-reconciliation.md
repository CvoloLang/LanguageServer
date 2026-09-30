# LanguageServer release history reconciliation

This document is the durable record of the Language Server's release history and
how its tag series relate to one another. It exists because the early history
mixed two different versioning schemes and produced several tagged-but-unreleased
attempts. Nothing here changes that history; it only records it accurately.

Tags are immutable. A version whose tag exists is considered *consumed* and is
never reused, even when it never became a published release.

## Scheme separation (current)

Three distinct quantities are reported by the server and recorded in every
bundle manifest. They are deliberately not the same value.

| Identity | Meaning |
| --- | --- |
| `LanguageServerVersion` | Independent product SemVer for the Language Server itself (`0.1.0-alpha.N`). |
| `ToolingVersion` | Exact pinned tooling version (`0.0.21.0`) plus its commit. |
| `CompilerCompatibilityLine` | Compatibility metadata describing which compiler line the tooling targets (`0.0.21`). |

The Language Server no longer derives its own version from the compiler/tooling
version. Earlier in its history it did, which is the origin of the
`v0.0.1x-alpha.y` tag series described below.

## Tag ledger

Commit SHAs are the commits the tags resolve to (`<tag>^{commit}`).

### Legacy independent LSP series: `v0.1.0-alpha.1` through `.7`

The first Language Server releases used an independent `0.1.0-alpha.N` product
version.

| Tag | Commit | Published GitHub Release |
| --- | --- | --- |
| `v0.1.0-alpha.1` | `178df2fc8cd8` | yes |
| `v0.1.0-alpha.2` | `277baf932623` | yes |
| `v0.1.0-alpha.3` | `c00b73838892` | yes |
| `v0.1.0-alpha.4` | `cf38d4255907` | yes |
| `v0.1.0-alpha.5` | `dec6eb0d3ab9` | yes |
| `v0.1.0-alpha.6` | `5e32ed07f8c0` | yes |
| `v0.1.0-alpha.7` | `98d2c3e6209c` | no |

`v0.1.0-alpha.7` is tagged but did not result in a GitHub Release. As with every
tagged version, it is consumed and is not reused.

### Compatibility-alias tags

While the server still derived its version from the tooling, each LSP release was
also tagged under a compiler-line-derived name of the form `v0.0.1x-alpha.y`.
Those alias tags point at exactly the same commits as the corresponding
`v0.1.0-alpha.N` tags.

| Alias tag | Aliases (same commit as) | Commit |
| --- | --- | --- |
| `v0.0.16-alpha.0` | `v0.1.0-alpha.1` | `178df2fc8cd8` |
| `v0.0.16-alpha.1` | `v0.1.0-alpha.2` | `277baf932623` |
| `v0.0.16-alpha.2` | `v0.1.0-alpha.3` | `c00b73838892` |
| `v0.0.18-alpha.0` | `v0.1.0-alpha.4` | `cf38d4255907` |
| `v0.0.19-alpha.0` | `v0.1.0-alpha.5` | `dec6eb0d3ab9` |
| `v0.0.19-alpha.1` | `v0.1.0-alpha.6` | `5e32ed07f8c0` |
| `v0.0.19-alpha.2` | `v0.1.0-alpha.7` | `98d2c3e6209c` |

These aliases are historical. New releases are tagged only under the independent
`v0.1.0-alpha.N` product version.

### Transition and release attempts

| Tag | Commit | Status |
| --- | --- | --- |
| `v0.1.0-alpha.8` | `855f4a037ef4` | Publicly tagged, but **unreleased**: no GitHub Release was produced. |
| `v0.1.0-alpha.9` | `d6b61620c35b` | Immutable tagged release **attempt** that did not become the final verified release. |
| `v0.1.0-alpha.10` | `0c0242ff38c6` | Immutable tagged release **attempt** that did not become the final verified release. |
| `v0.1.0-alpha.11` | `647dbd8b691c` | First fully normalized, successfully published, independently verified release. |

`v0.1.0-alpha.9` (annotated tag object `f465af4f66d6011e168126919b3c2e83e478a573`)
and `v0.1.0-alpha.10` (annotated tag object
`573a558f3f9ecd1f68009ec26b07229fed87c04f`) each reached the release workflow but
failed before a GitHub Release was created. They are recorded here as failed
release attempts, not as releases. Their tags remain in place and those versions
are consumed.

`v0.1.0-alpha.11` (annotated tag object
`4929bcc31dd238c1cc606c129b021fea2014ceb5`) is the first release produced by the
fully normalized pipeline: it published all eleven assets, and its artifacts were
independently verified against the live release (checksums, bundle manifests,
layout, entrypoints, and a runtime smoke test). `master` was fast-forwarded to
this exact commit only after that verification.

## The transient `v0.0.21-alpha.0` identity

`v0.0.21-alpha.0` was a transient compiler-derived identity produced while the
server still took its version from the tooling. It was briefly published, then
removed before any VS Code Marketplace release depended on it. It is not part of
the supported tag history:

- it does not exist on `origin`;
- there is no GitHub Release for it; and
- no tracked file references it.

Its local tag (annotated tag object
`72dd481f7f56e4fb96dfdda1dad6ebb7c8baf4b0`, which peeled to the old
`master` commit `ec6af61e7583fb9e5187b734983989b3e1c48984`) has been deleted. No
other historical tag was removed or moved.

## Published releases

The following GitHub prereleases currently exist. Each carries the eleven
standard release assets (five platform archives, five manifest sidecars, and the
release-level `SHA256SUMS`).

`v0.1.0-alpha.1`, `v0.1.0-alpha.2`, `v0.1.0-alpha.3`, `v0.1.0-alpha.4`,
`v0.1.0-alpha.5`, `v0.1.0-alpha.6`, `v0.1.0-alpha.11`.
