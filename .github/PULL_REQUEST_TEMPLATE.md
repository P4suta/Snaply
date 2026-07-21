## Summary

## Linear

Closes DEV-___
<!-- Links this PR to the Linear issue and closes it on merge; requires the Linear GitHub integration. -->

## Verification

- [ ] `dotnet test tests/Snaply.Tests/Snaply.Tests.csproj -c Release --no-restore`
- [ ] x64 Release build has no warnings
- [ ] `dotnet format Snaply.slnx --verify-no-changes --no-restore`
- [ ] Updated lock files when dependencies changed
- [ ] Added no unused feature, public API, capability, or dependency
- [ ] PR title follows Conventional Commits
