---
date: 2026-10-29
---

# Verified draft publication

## Status

Proposed.

## Context

The release-please workflow creates a draft and dispatches the protected release controller on main with a tag and commit SHA.
Manual dispatch can also select a valid tagged commit without an existing release.
GitHub Immutable Releases prevents changes to published assets and tags.
A retry must preserve the original package bytes and existing release notes instead of replacing assets.
The former publication job executed candidate scripts with its write token available in the environment.

## Decision

Run extended candidate checks before signing and require the release controller to run on main.
Build the private publisher from the controller SHA in a separate read-only job.
Package signed candidate artifacts in a read-only job.
Pass the publisher and package through immutable artifacts from the same workflow run to a fresh publication job.
That job checks out only the controller revision, executes no candidate scripts, and supplies its token only to the publisher step.
Keep the protected release environment and provenance and SBOM attestations.

Use the [publisher core](../../tools/release-publisher/src/core.rs) to require the exact eight package names and refuse duplicate, unexpected, mismatched, or published targets.
The [GitHub adapter](../../tools/release-publisher/src/github.rs) checks the remote tag, selected commit, main ancestry, and project version.
It hashes local files and downloaded remote asset bytes instead of treating the checksum file as the complete inventory.
The adapter accepts regular files of at most two GiB each and at most sixteen nested annotated tags.
It refuses malformed API responses and incomplete pagination.

The default command prints a plan without remote writes.
The apply command creates a draft only when none exists, keeps matching draft assets, uploads missing assets without replacement, and verifies all bytes before changing only the draft flag.
Existing draft title, notes, and prerelease metadata stay unchanged.
An uncertain write or failed readback stops execution and reports an incomplete result.
A later explicit invocation can inspect or resume the remaining draft.
The publisher refuses a published target even after an earlier invocation might have completed publication.
The publisher never creates tags, replaces or deletes assets, or retries a failed mutation.

Keep fast formatting, strict Clippy, and deterministic tests in the existing CI hygiene job and local hook.
Audit the locked publisher dependencies using a fixed RustSec snapshot before building the release controller binary.
Run source-bound Kani decision harnesses and negative controls only in the extended release gate.
Publisher proofs check the controller revision that produces the binary, while native app checks use the selected candidate SHA.
The controller build also runs the fast publisher checks on Windows before compiling the publication binary.
The harnesses cover scalar asset admission, publication eligibility, every subset of one canonical eight-file draft, byte mismatches, inventory refusals, and forty-byte SHA validation.
The inventory harnesses fix the repository and version tag, and the SHA harness fixes its forty-byte input length.
Other repository and tag grammar, arbitrary inventory sizes and names, and other SHA lengths have deterministic test controls but no claimed proof.
They don't prove GitHub, process execution, filesystem stability, cryptographic collision resistance, or the complete workflow.

## Consequences

Partial drafts can resume without losing uploaded bytes or release-please metadata.
The publisher adds a private Rust dependency graph and a small controller build at release time.
Production publication, approvals, and hosted platform behavior still need release evidence.
The workflow concurrency group serializes its own runs for a tag.
GitHub offers no atomic transaction across tag reads, asset uploads, and publication.
The procedure assumes no independent writer changes the release or package between validation and the following operation.
Identity and byte readbacks detect observed changes but don't remove that race.

A single `gh release create` command can't resume the draft that release-please creates.
Using `--clobber` or deleting and recreating a release can destroy prior assets and conflicts with immutable publication.
Building the publisher from the candidate would execute candidate-controlled code with the publication token.
Keeping the former action leaves the write-token exposure and retry contract unresolved.
