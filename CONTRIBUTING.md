# Contributing to Snaply

Thanks for your interest in improving Snaply. This guide covers the local
setup, the conventions the project enforces, and what CI expects from a PR.

## Toolchain

Snaply provisions its toolchain with [mise](https://mise.jdx.dev/) — the pinned
.NET 10 SDK and [just](https://github.com/casey/just) are declared in
`mise.toml`. Do **not** install the SDK globally; let mise supply it.

```sh
mise install            # one-time: install the pinned tools
just                    # list all recipes
just build              # build the solution
just test               # headless Core unit tests
just run                # build + launch the app
```

## Architecture principles

Snaply's single most important quality bar is **architectural cleanliness**.
Before adding code, make sure it respects the boundaries:

- **`Snaply.Core` has zero dependencies** and never references a platform type
  (no WinUI, no Win32, no `System.Drawing`). It is pure logic over immutable
  records, and its tests run on Linux. If your change needs a platform API, it
  belongs in `Snaply.Platform`, behind a Core-defined port.
- **`Snaply.Platform`** holds the adapters (WGC, Win2D, Win32, tray). It
  implements the ports declared in Core.
- **`Snaply.App`** is the WinUI 3 shell and the DI composition root only —
  ViewModels never reference WinUI types directly (strings come in via
  `IUiStrings`, etc.).
- Failures are explicit via the `Result` type — no silent catches.
- One service, one responsibility. No god objects.

When a design decision is ambiguous, choose beauty, separation, and
testability.

## Code style & analyzers

The whole solution builds with `TreatWarningsAsErrors` and a strict analyzer
set (.NET analyzers + Roslynator + Meziantou + StyleCop; see
`Directory.Build.props` and `.editorconfig`). CI runs `dotnet format
--verify-no-changes`, so format locally before pushing:

```sh
dotnet format
```

## Commit messages

Commits and PR titles follow
[Conventional Commits](https://www.conventionalcommits.org/) — this is what
drives `release-please` (versioning + CHANGELOG) and is enforced by the
`pr-title` CI check. Examples:

```
feat: add scrolling-window capture
fix: keep clipboard copy on the UI thread
docs: document the release process
chore(deps): bump CommunityToolkit.Mvvm
```

## Before you open a PR

- `just test` is green (Core tests + coverage).
- `just build-app` is clean (0 warnings under `TreatWarningsAsErrors`).
- `dotnet format --verify-no-changes` reports no diffs.
- `dotnet restore --locked-mode` succeeds (if you changed dependencies, commit
  the updated `packages.lock.json`).

CI runs the full matrix (hygiene, tests, Windows build, CodeQL, supply-chain
scans) and gates merges on the aggregated `ci-required` check.

## What CI does *not* run (and why)

Some checks from the sister project [find-my-files](https://github.com/P4suta/find-my-files)
are Rust-specific and have no meaningful .NET equivalent, so they are
deliberately omitted: fuzzing (`cargo-fuzz`), `cargo-deny`, `cargo-machete`,
and TOML formatting. Mutation testing is covered by Stryker.NET in the nightly
workflow instead.
