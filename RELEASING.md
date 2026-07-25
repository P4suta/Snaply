# Releasing

1. Run `./scripts/verify.ps1`.
2. Confirm repository variable `MSIX_PUBLISHER`, release variable `SIGNER_SUBJECT_CONTAINS`, and the four SSL.com eSigner secrets are present.
3. Run the release workflow with `publish=false`.
4. Require green signing, SBOM/license/vulnerability checks, WACK, and x64/ARM64 portable/MSIX QA: three capture modes, automatic save/copy, two launches, upgrade, uninstall.
5. Merge the Release Please PR; publish only the immutable tag created from `main`.

GitHub settings are checked with `./scripts/sync-github-settings.ps1 -Check`; credentials are never stored in the repository.
