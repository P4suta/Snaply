# Releasing

Snaply uses [release-please](https://github.com/googleapis/release-please) to
automate versioning, the changelog, and the GitHub Release. Humans never
hand-pick or hand-edit a version.

## The flow

1. **Conventional Commits land on `main`.** `feat:` → minor, `fix:` → patch,
   `feat!:` / `BREAKING CHANGE:` → major.
2. **release-please keeps a "Release PR" open** (labelled `autorelease:
   pending`) that bumps `<Version>` in `Directory.Build.props`, updates
   `CHANGELOG.md`, and syncs `.release-please-manifest.json`.
3. **A human approves the release** by adding the `release: approved` label
   (the `release-gate` CI check enforces this) and merges the Release PR.
4. **release-please creates the GitHub Release as a draft** and dispatches
   `release.yml`.
5. **`release.yml` builds → signs → publishes:**
   - `build` — `just publish vX.Y.Z` assembles the self-contained bundle and
     generates the SBOM. No secrets.
   - `sign` — Authenticode-signs Snaply's own PEs (SSL.com eSigner) in the
     approval-gated `release` environment, then verifies the signatures.
   - `publish` — re-verifies signatures at the irreversible boundary, packages
     `snaply-vX.Y.Z-win-x64.zip` + `SHA256SUMS.txt`, writes Sigstore
     build-provenance + SBOM attestations, attaches everything to the draft, and
     publishes it (which creates the `vX.Y.Z` tag).

## Activating release-please

release-please runs as a GitHub App so its commits are signed. It is **dormant**
until two secrets are set (the workflow runs green and no-ops without them):

- `RELEASE_PLEASE_CLIENT_ID`
- `RELEASE_PLEASE_PRIVATE_KEY`

Create a GitHub App with `contents: write` and `pull_requests: write`, install
it on the repo, and store its client id + private key as secrets in the
`release-please` environment.

## Manual signing smoke test

`release.yml` can be dispatched by hand with `publish=false` and
`tag_name=main`: it builds, signs (if the signing secrets are present, otherwise
warns and passes through unsigned), and verifies — but creates **no** Release
and **no** attestations. Safe under immutable releases.

See [SIGNING.md](SIGNING.md) for the signing setup and
[SUPPLY_CHAIN.md](SUPPLY_CHAIN.md) for the verification story.
