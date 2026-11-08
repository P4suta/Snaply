---
doppler_automation_app: release_please
---

# Credential delivery

The release App credentials use Doppler's [native GitHub synchronization](https://docs.doppler.com/docs/github-actions).
The [delivery decision](adr/sync-release-app-credentials-to-the-protected-environment.md) governs the cutover.
Canonical values remain in Doppler, and a purpose-specific config references the automation fields.
The [manifest](../automation/release-app-sync.toml) selects the repository, environment, and field mapping.

The release-please environment admits main and prevents administrator bypass.
RELEASE_PLEASE_CLIENT_ID references GITHUB_APP_CLIENT_ID, and RELEASE_PLEASE_PRIVATE_KEY references GITHUB_APP_PRIVATE_KEY.
The workflow passes each named secret only to the repository-scoped App token action.
Tokens request only the consumer's required permissions.
The shared private key retains authority across the App's installations.
GitHub stores copies, and protected main and environment policies replace Doppler workflow and event claims.

The manual **Verify credential access** workflow creates a read-only App token and revokes it during cleanup.
Run the workflow from reviewed main after merging.
The default invocation verifies only App access.
Selecting the verify_signing input also runs the existing signing credential check.
The verification performs no signing or publication.

Windows signing keeps Doppler OIDC through DOPPLER_SIGN_WINDOWS_IDENTITY_ID in the release environment.
Release approvals and attestation authentication remain unchanged.

Rotate canonical values in Doppler and verify synchronization and authentication before retiring the previous key.
Keep the existing automation identity until every consumer has migrated.
Pause the release-please workflow during the cutover and restore its previous enabled state after read-only authentication succeeds.
Keep credential values and authentication state out of source and logs.
Credential migration creates no release, tag, or signature.
