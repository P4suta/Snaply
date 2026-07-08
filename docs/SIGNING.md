# Code signing

Snaply's release binaries are Authenticode-signed so Windows SmartScreen does
not warn users, and so a download can be traced to a known signer. Signing is
**dormant** until the signing secrets are configured — until then, releases ship
unsigned (with a `::warning::`) but still carry Sigstore provenance
attestations.

## What gets signed

Every one of Snaply's own PE files — see `scripts/sign-map.ps1` (the single
source of truth; `verify-signatures` reads the same map so the two never drift):

- `Snaply.exe` — the Native AOT launcher at the bundle root
- `app/Snaply.App.exe` — the WinUI apphost
- `app/snaply.exe` — the CLI / MCP-server apphost
- `app/Snaply.App.dll`, `app/Snaply.Core.dll`, `app/Snaply.Application.dll`,
  `app/Snaply.Platform.dll` — the first-party managed assemblies (the actual
  application logic; managed assemblies are PE files and are signed too)

The bundled .NET / Windows App SDK / ASP.NET Core runtime DLLs are already
Microsoft-signed and are deliberately **not** re-signed.

## Provider: SSL.com eSigner

`release.yml`'s `sign` job uses the official `SSLcom/esigner-codesign` action
(`batch_sign`). CodeSignTool scans then signs each PE and timestamps it via
SSL.com's RFC 3161 TSA, so the signatures outlive the certificate.

Configure these secrets in the approval-gated `release` environment:

| Secret | Purpose |
|---|---|
| `ES_USERNAME` | SSL.com eSigner account username |
| `ES_PASSWORD` | account password |
| `CREDENTIAL_ID` | the signing credential id |
| `ES_TOTP_SECRET` | the TOTP secret for automated 2FA |

Also set `SIGNER_SUBJECT_CONTAINS` (the `release` environment variable) to a
substring of your certificate's subject (e.g. `CN=Your Name`); the verify step
asserts it so a different certificate cannot pass silently. It is **required**
when signing: `verify-signatures` refuses to run (fails closed) if it is empty,
so the identity assertion can never be silently skipped.

## The safety net

- The `sign` and `publish` jobs run in the `release` environment, which requires
  a human approval — two gates ("sign this?" and "release this?").
- `verify-signatures` (a shared composite action) checks chain + RFC 3161
  timestamp + signer subject.
- The `publish` job **re-verifies** the bundle is signed before creating the
  immutable Release. A missing or misconfigured secret fails the release rather
  than shipping unsigned.

## Alternative providers

If you later switch to [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)
or a self-hosted certificate, only the `sign` job's signing step and the
`SIGNER_SUBJECT_CONTAINS` assertion change — the stage/collect map and the
verification gate stay the same.
