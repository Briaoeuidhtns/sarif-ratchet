# sarif-ratchet

`sarif-ratchet` is a tool for managing static analysis baselines using the SARIF format. It helps you implement a "ratchet" mechanism where you can prevent new static analysis errors from being introduced while allowing existing ones (the baseline) to pass.

## Key Features

- **Path Normalization**: Automatically handles relative paths and forward slashes to ensure baselines are portable across Windows, Linux, and macOS.
- **Strictness Fallbacks**: Intelligently identifies results using Fingerprints, Logical Locations, or Regions.
- **Multiple Output Formats**: Supports Spectre.Console (UI), JSON, MessagePack, and GitHub Actions annotations.
- **Nix Support**: Fully compatible with Nix for hermetic builds and execution.
- **Multi-Platform Distribution**: Available via NuGet, npm, and Nix.

## Installation

### Using Nix
```bash
nix run github:briaoeuidhtns/sarif-ratchet -- --help
```

### Using npm
```bash
npx sarif-ratchet --help
```

### Using NuGet
```bash
dotnet tool install -g sarif-ratchet
```

## Usage

### Compare Results
Compare a new SARIF file against a baseline. If new errors are found, the tool exits with code 1.

```bash
sarif-ratchet compare baseline.sarif new.sarif --root src/
```

### Update Baseline
Update the baseline file to include only the errors that still exist in the new SARIF file. This is useful for "ratcheting" down the allowed error count as you fix them.

```bash
sarif-ratchet update baseline.sarif new.sarif --root src/ --strip
```

### Sanitize Baseline
Remove absolute paths and local user information from a SARIF file before committing it to a repository.

```bash
sarif-ratchet sanitize baseline.sarif --root src/
```

### GitHub Actions Integration
Use the `--format github` option to emit workflow commands that show up as annotations in your PRs.

```yaml
- name: Run Sarif Ratchet
  run: npx sarif-ratchet compare baseline.sarif new.sarif --format github
```

## Configuration

- `--root <PATH>`: Specify the root directory for path normalization. Highly recommended for portability.
- `--strictness <LEVEL>`: Set the matching strictness (`Fingerprint`, `Location`, or `Region`).
- `--strip`: (Update only) Remove non-essential metadata from the baseline to keep it small.

## Development

This project uses Nix for development.

```bash
nix develop
dotnet build
```

To update dependencies for the Nix build:
```bash
nix run .#fetch-deps
```
