# Supply-chain security

Snaply treats its build and release pipeline as part of the attack surface.
This document describes the controls that protect the path from source to a
downloaded binary.

## Locked dependency resolution

Every project sets `RestorePackagesWithLockFile=true` (in
`Directory.Build.props`) and commits a `packages.lock.json`. CI restores with
`--locked-mode`, so a build fails rather than silently re-resolving to a
different dependency graph. When you change a `PackageReference`, run
`dotnet restore` and commit the updated lock file.

## Pinned GitHub Actions

Every `uses:` in `.github/workflows/**` is pinned to a full commit SHA, not a
floating tag. Dependabot (`.github/dependabot.yml`) opens weekly PRs to bump
those pins, so the pin is current without ever trusting a mutable tag.

## Least-privilege workflows

Each workflow declares `permissions: contents: read` at the top level; a job
that needs more (e.g. `security-events: write` to upload SARIF) requests the
extra scope at the job level only. This is what OpenSSF Scorecard's
Token-Permissions check rewards.

## Vulnerability & posture scanning

- **CodeQL** (`codeql.yml`) — static analysis of the C# app and the workflow
  YAML itself, on push/PR and weekly.
- **NuGet audit** (`nuget-audit.yml`) — `dotnet list package --vulnerable`
  (and `--deprecated`), weekly and on PRs.
- **OpenSSF Scorecard** (`scorecard.yml`) — weekly posture scan; results feed
  the README badge and the Security tab.
- **SBOM + osv-scanner** (`sbom-monitor.yml` and the release build) — a
  CycloneDX SBOM is generated from the resolved NuGet graph of the whole shipped
  solution (both apphosts — the WinUI app **and** the CLI/MCP server — with test
  projects excluded) and scanned for known vulnerabilities. The bundled .NET and
  ASP.NET Core runtime travel via a shared `FrameworkReference` (pinned by the
  SDK, not a NuGet package), so their versions aren't listed as SBOM components;
  they are fixed by the `mise.toml` SDK pin instead.

## Signed, attested releases

Release artifacts are:

- **Authenticode-signed** — the first-party PE files (`Snaply.exe`,
  `app\Snaply.App.exe`) are signed via SSL.com eSigner in an approval-gated
  `release` environment. Signing is verified (chain + RFC 3161 timestamp +
  signer subject) before the release is published; an unsigned bundle fails at
  the irreversible boundary. (Dormant until the signing secrets are configured
  — see [SIGNING.md](SIGNING.md).)
- **Provenance-attested** — keyless [Sigstore](https://www.sigstore.dev/)
  build-provenance and SBOM attestations are written via the workflow's OIDC
  token (no stored keys). Verify a download:

  ```sh
  gh attestation verify snaply-*-win-x64.zip --repo P4suta/Snaply
  ```

See [RELEASING.md](RELEASING.md) for the end-to-end release flow.
