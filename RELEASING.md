# Releasing

Release Please owns versions, tags, release notes, and the release pull request. Merging that pull request starts the protected release workflow.

The `release` environment must require approval and provide:

- `ES_USERNAME`, `ES_PASSWORD`, `CREDENTIAL_ID`, and `ES_TOTP_SECRET`
- `SIGNER_SUBJECT_CONTAINS`: the expected certificate subject fragment

The repository variable `MSIX_PUBLISHER` must contain the exact distinguished name of the SSL.com signing certificate.

The workflow builds x64 and ARM64 portable payloads and single-project MSIX packages, bundles and signs them, verifies RFC 3161 timestamps and signer identity, generates an SPDX SBOM and SHA-256 checksums, scans vulnerabilities and licenses, and creates GitHub attestations.

Publication remains blocked until the release candidate passes WACK and the release-artifact journeys on clean Windows 11 24H2 x64 and ARM64 machines: install/extract, launch, all capture modes, automatic copy/save, Save As, restart, upgrade, and uninstall.

Repository rulesets, protected tags, signed commits, secret scanning with push protection, private vulnerability reporting, CodeQL, Dependabot, and environment approval must be enabled in GitHub itself. Checked-in files are not evidence that these settings are active.
