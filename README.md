# sarif-ratchet

Manage SARIF static-analysis baselines: reject newly introduced findings while allowing existing findings, and reduce the baseline as findings are fixed.

## Installation

```bash
# .NET global tool (requires a supported .NET 8, 9, or 10 runtime)
dotnet tool install -g SarifRatchet

# npm: self-contained .NET 10 binaries; no separate .NET installation needed
npx sarif-ratchet --help

# Nix
nix run github:briaoeuidhtns/sarif-ratchet -- --help
```

The npm package supports Windows, Linux (glibc), and macOS on x64 and arm64. Its Node.js launcher selects the matching binary and preserves its exit status. Alpine/musl and other architectures are not supported by this npm distribution. Node.js 20 or newer is required. Allow the package's install script: it sets native executable permissions on POSIX hosts, including for archives built on Windows; it does not download or build anything.

## Usage

```bash
sarif-ratchet compare baseline.sarif new.sarif --root src/
sarif-ratchet update baseline.sarif new.sarif --root src/ --strip
sarif-ratchet sanitize baseline.sarif --root src/
sarif-ratchet compare baseline.sarif new.sarif --format github
```

`compare` exits with code 1 when new findings are found. `update` retains only matched baseline result instances; it does not add newly introduced findings. Duplicate findings count separately: two current instances against one baseline instance means one new finding. Results are matched within their tool-component, rule, and normalized file identity; findings from different tools do not cancel each other out.

`compare --check` also exits with code 1 when findings have been fixed and the baseline needs updating. `--warn-removed` highlights fixed findings (or emits warning annotations with GitHub output); it does not change the exit status.

Counts describe SARIF result instances, not distinct rules or distinct tools. Repeated runs from the same tool and duplicate results contribute their full multiplicity; the CLI does not deduplicate findings into a set.

### Matching and paths

- `--strictness Fingerprint` is the default. Negotiate one tier per tool/rule/file group across **both** logs: complete fingerprints when every member has them, otherwise partial fingerprints when every member has those, otherwise logical identity when every member has one, otherwise exact region identity. Complete and partial fingerprints never compare against each other. There is no automatic fallback to Loose.
- `--strictness Logical` uses logical identity where available throughout the group, otherwise exact region identity.
- `--strictness Exact` uses region identity.
- `--strictness Loose` explicitly matches only tool/rule/file identity, still preserving duplicate counts.
- `--root <PATH>` normalizes paths relative to that directory; relative roots resolve against the working directory. Artifact indices, URI base-ID chains, logical-location indices, URI escaping, slash variants, and dot segments are resolved. External non-file URIs retain their identity. Invalid unresolved or cyclic references fail explicitly.
- Fingerprint key/value maps are canonicalized independently of dictionary order.

### Sanitizing and stripping

`sanitize` resolves every artifact location before removing URI base-ID tables and rewrites file paths relative to `--root` (the current working directory by default). Absolute file paths outside that root are rejected before writing, rather than silently leaked or conflated. External non-file URIs are preserved.

Invocation entries remain to preserve reference indices, but local invocation metadata (commands, arguments, response files, environment, machine/account information, executable, working directory, streams, and logs) is removed. Version-control provenance is removed. Independently, `update --strip` removes bulky content such as snippets, artifact contents, logs, and flows, while retaining referenced artifact/logical-location tables.

**This is not a general PII redactor.** Arbitrary messages and custom-property strings are not scrubbed. Review the output before publishing it.

### Output

Use `--format Spectre`, `Json`, `MessagePack`, or `GitHub` to select terminal UI, JSON, MessagePack, or GitHub annotations. Diagnostic brackets are rendered literally in terminal output, and annotation properties/messages are escaped for the GitHub workflow-command protocol. For example:

```yaml
- name: Compare SARIF baseline
  run: npx sarif-ratchet compare baseline.sarif new.sarif --format github
```

## Development and local artifacts

```bash
nix develop
# Or install the .NET 10 SDK and Node.js.
dotnet build
dotnet test --solution SarifRatchet.slnx
npm run pack:artifacts
npm run smoke:artifacts
```

`pack:artifacts` (also available as `build:artifacts`) restores the pinned local NBGV tool, derives the release version from Git, publishes self-contained **net10.0** binaries for all six npm targets, packs the multi-target NuGet tool, and creates an npm tarball in `artifacts/`. The staged npm manifest receives exactly the NBGV NuGet package version; the source manifest is not rewritten. The npm payload is allowlisted to the launcher, permission setup script, binaries, manifest, and README, not source or fixtures. These commands never publish, tag, push, or commit. They require network access for package/runtime downloads and a full Git history for release versioning.

The repository's source npm manifest is private to prevent accidental publication of an unbuilt checkout; only the staged artifact manifest is publishable. Sample-project compiler logs are generated under ignored `obj/` directories, not over the tracked SARIF fixtures.

`smoke:artifacts` installs both local packages into a temporary directory, checks the npm version and payload, checks all six binaries, runs the host npm and NuGet commands, and checks npm failure-exit propagation. It does not contact a publication endpoint. On non-host targets, executable validation requires running on those operating systems/architectures; a single host smoke run does not prove every cross-published binary executes.

CI builds and locally installs **both** artifacts before any publication step. Release tags must equal `v` plus the NBGV-derived version. Publication consumes the already-validated files without lifecycle builds.

### Nix dependencies

The flake exports a dependency-regeneration app:

```bash
nix run .#fetch-deps
nix build
```

This wraps the package's nixpkgs `fetch-deps` passthru script. `deps.json` describes the net10.0 dependency closure, including build-only Nerdbank.GitVersioning. Its hashes are SHA-256 hashes of the actual NuGet package bytes. Regenerate it when changing dependencies and verify with Nix on a supported host.

Nix builds from a source archive without `.git`: it passes `SarifRatchetSourceVersion`, derived from the `version.json` major/minor plus `.0` and the available source revision. This disables NBGV build assets (but keeps its pinned dependency in the restore closure) and supplies archive-safe SDK version properties. It is not the Git-height-derived NuGet/npm release version.

MessagePack is pinned to stable 3.1.12, beyond the affected 3.x ranges in [GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h) (fixed in 3.1.9) and the [MessagePack NuGet advisories](https://github.com/advisories?query=ecosystem%3Anuget+MessagePack).
