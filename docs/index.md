# Snaply documentation

Snaply is a modern, clean-architecture screenshot tool for Windows 11. This site
holds the developer documentation; for a user-facing overview and screenshots,
see the [README](https://github.com/P4suta/Snaply#readme).

## Contents

- **[Architecture](ARCHITECTURE.md)** — the ports & adapters layering and the
  rules that keep `Snaply.Core` platform-independent.
- **[Releasing](RELEASING.md)** — how a version is cut (release-please →
  build → sign → publish).
- **[Signing](SIGNING.md)** — the Authenticode signing setup and how to
  activate it.
- **[Supply chain](SUPPLY_CHAIN.md)** — locked dependencies, SBOMs,
  attestations, and scanning.
- **[API reference](api/)** — generated docs for `Snaply.Core`.

## Building the docs locally

```sh
dotnet tool install -g docfx
just docs      # builds docs/docfx.json and serves it
```
