# Cvolo.LanguageServer

Language Server Protocol (LSP 3.17) implementation for the Cvolo programming language.

## Repository layout

```
build/fetch-tooling.ps1       Windows tooling bootstrapper (temporary infrastructure, not a package manager)
build/fetch-tooling.sh        POSIX tooling bootstrapper
build/release/                Bundle manifest, packaging and release verification scripts
tooling.version               Pinned Cvolo.Compiler.Tooling version (single line, no floating versions)
compiler-compatibility.version  Compiler line this release supports (single line)
src/Cvolo.LanguageServer      Server source (Program.cs, Protocol/, Logging/)
tests/Cvolo.LanguageServer.Tests  Protocol and end-to-end tests
docs/versioning.md            The three identities and the product version progression
docs/bundle-payload.md        Release bundle layout, manifest schema and staging rules
artifacts/tooling/<version>/  Downloaded tooling bundle (gitignored, immutable by version)
artifacts/tooling-dir.props   Generated after bootstrap (gitignored, consumed by MSBuild)
```

## Prerequisites

- .NET SDK 10.0 or newer (see `global.json`-free install: `dotnet --list-sdks`)

## Restore the tooling bundle

The server has exactly one compile-time dependency on the Cvolo compiler ecosystem:
`Cvolo.Compiler.Tooling.dll`. Its version is pinned in `tooling.version` and the
compatible bundle is fetched at build time. These scripts are temporary
bootstrap infrastructure -- they download a single pinned release, verify its
SHA256 checksums and manifest, and place it under `artifacts/tooling/<version>/`.
They are not a package manager and do not resolve versions.

Windows:

```powershell
./build/fetch-tooling.ps1
```

POSIX:

```sh
./build/fetch-tooling.sh
```

The script reuses an existing verified cache in `artifacts/tooling/<version>/`
when present and skips the network entirely.

## Build and test

```sh
# after fetch-tooling has run once:
dotnet build src/Cvolo.LanguageServer.slnx
dotnet test tests/Cvolo.LanguageServer.Tests/Cvolo.LanguageServer.Tests.csproj
```

CI builds use locked NuGet restore (`ContinuousIntegrationBuild=true`).

## Running the server

```
cvolo-language-server [options]

Options:
  --stdio        Communicate over stdio using JSON-RPC (default and only transport)
  --log <path>   Write logs to <path> (logging is off by default)
  --verbose      Enable verbose logging, including raw JSON-RPC payloads
  --help         Show help and exit
  --version      Print version and provenance, then exit
  --json         Machine-readable output; only valid together with --version
```

stdout is reserved for the JSON-RPC protocol only. Diagnostics go to stderr
and/or the optional log file.

```
$ cvolo-language-server --version
Cvolo Language Server 0.1.0-alpha.12
Commit: 0000000000000000000000000000000000000000
Tooling: 0.0.21.1
Compiler compatibility: 0.0.21
```

`--version --json` emits the same provenance as a single JSON object using the field
names of `bundle-manifest.json`.

## Versioning

The compiler, `Compiler.Tooling`, the LanguageServer and the VS Code extension are
separate products with separate versions. See `docs/versioning.md`.

- LanguageServer product version comes from the `Version` MSBuild property
  (`0.1.0-alpha.N`). It is never derived from the compiler or tooling version.
- Tooling version comes from `tooling.version`.
- Compiler compatibility line comes from `compiler-compatibility.version`.

The bootstrapper hard-fails unless those two metadata files agree with the pinned
`tooling.manifest.json`, and the release pipeline re-applies the same check to the
files actually shipped in each archive. `--version` works without a tooling bundle
present.
