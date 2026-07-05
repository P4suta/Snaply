## Summary

<!-- What does this change, and why? -->

## Checklist

- [ ] PR title follows [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `perf:`, `docs:`, …) — it drives release-please and the release notes
- [ ] `just test` passes (Core unit tests + coverage)
- [ ] `just build-app` is clean (0 warnings under `TreatWarningsAsErrors`)
- [ ] `dotnet format --verify-no-changes` reports no diffs
- [ ] If dependencies changed: committed the updated `packages.lock.json`
- [ ] Respected the architecture boundaries (Core stays platform-independent)

See [CONTRIBUTING.md](../CONTRIBUTING.md).
