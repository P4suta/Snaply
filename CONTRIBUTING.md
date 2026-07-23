# Contributing

Use Windows 11 24H2 or later and the .NET SDK pinned by `global.json`.

```powershell
dotnet restore Snaply.slnx --locked-mode
dotnet build src/Snaply.App/Snaply.App.csproj -c Release -p:Platform=x64 --no-restore
dotnet test tests/Snaply.Tests/Snaply.Tests.csproj -c Release --no-restore
$env:Configuration = 'Release'
dotnet format Snaply.slnx --verify-no-changes --no-restore
```

Before a pull request:

- Keep the product GUI-only and local-only.
- Add no public API, capability, dependency, setting, or abstraction without a current product need.
- Add tests for behavior and non-trivial calculations.
- Keep comments for ABI, ownership, lifetime, security, or non-obvious algorithms only.
- Use Conventional Commits and keep the branch green with no warnings or skipped required tests.

Release packaging and UI automation are documented in [RELEASING.md](RELEASING.md).
