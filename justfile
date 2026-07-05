# Snaply — task runner. `just` is provisioned by mise (see mise.toml). Recipes call
# the toolchain through `mise exec --` so `dotnet` resolves to the pinned .NET 10 SDK
# regardless of which shell launched `just` (mise activation lives in the profile,
# which the -NoProfile recipe shell skips; `mise.exe` itself is on the base PATH).
# Run `just` with no args to list recipes.

set windows-shell := ["pwsh", "-NoProfile", "-Command"]

# The unpackaged, self-contained apphost produced by a Debug x64 build.
apphost := "src/Snaply.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Snaply.App.exe"

default:
    @just --list --unsorted

# ── Build ────────────────────────────────────────────────────────────────

# Mixed platforms: don't pass -p:Platform at the solution level — each project
# builds its own default (Core/Tests AnyCPU, App/Platform x64).
# Build the whole solution (Debug).
build:
    mise exec -- dotnet build Snaply.slnx

# Build just the app (x64).
build-app:
    mise exec -- dotnet build src/Snaply.App/Snaply.App.csproj -p:Platform=x64

# ── Run ──────────────────────────────────────────────────────────────────

# Unpackaged self-contained → run the apphost exe directly (winapp run is for
# packaged apps).
# Build then launch the app.
run: build-app
    Start-Process "{{apphost}}"

# ── Test ───────────────────────────────────────────────────────────────────

# Headless Core unit tests (no display/GPU needed — the architecture fitness goal).
test:
    mise exec -- dotnet test tests/Snaply.Tests/Snaply.Tests.csproj

# ── Release / distribution ─────────────────────────────────────────────────

# Layout: build/dist/Snaply/{Snaply.exe launcher, README.txt, app/ = self-contained app}.
# One obvious exe at the root; the ~200 runtime files stay in app/.
# Assemble the self-bundled distributable into build/dist/Snaply. Pass a version
# (e.g. `just publish v0.1.0`) to stamp a stable release build.
publish version="":
    mise exec -- pwsh -NoProfile -File scripts/publish.ps1 {{ if version != "" { "-Version " + version } else { "" } }}

# Assemble the bundle, then zip its contents into build/package/ + SHA256SUMS.txt.
# With a version, the zip is named snaply-vX.Y.Z-win-x64.zip.
package version="":
    mise exec -- pwsh -NoProfile -File scripts/publish.ps1 -Package {{ if version != "" { "-Version " + version } else { "" } }}

# Zip an already-built (e.g. signed) bundle without rebuilding it — the release
# pipeline packages the signed bundle this way so signatures survive.
package-existing version="":
    mise exec -- pwsh -NoProfile -File scripts/publish.ps1 -Package -SkipBuild {{ if version != "" { "-Version " + version } else { "" } }}

# ── Release signing (CI: release.yml) ───────────────────────────────────────

# Stage Snaply's own PEs out of the bundle into build/sign-stage for batch signing.
sign-stage:
    mise exec -- pwsh -NoProfile -File scripts/sign-stage.ps1

# Map the signed PEs in build/signed back over the bundle.
sign-collect:
    mise exec -- pwsh -NoProfile -File scripts/sign-collect.ps1

# ── Docs & SBOM ─────────────────────────────────────────────────────────────

# Generate the CycloneDX SBOM into build/sbom (needs `dotnet tool install -g CycloneDX`).
sbom:
    mise exec -- pwsh -NoProfile -Command "New-Item -ItemType Directory -Force build/sbom | Out-Null; dotnet CycloneDX src/Snaply.App/Snaply.App.csproj --output build/sbom --filename snaply.cdx.json --output-format Json --spec-version 1.6 --runtime win-x64"

# Build + serve the docfx documentation site locally (needs `dotnet tool install -g docfx`).
docs:
    mise exec -- pwsh -NoProfile -Command "docfx docs/docfx.json --serve"

# ── Formatting ──────────────────────────────────────────────────────────────

# Apply code-style fixes across the solution.
fmt:
    mise exec -- dotnet format Snaply.slnx

# Verify formatting without changing files (CI hygiene gate).
fmt-check:
    mise exec -- dotnet format Snaply.slnx --verify-no-changes

# ── Housekeeping ───────────────────────────────────────────────────────────

# Remove build outputs.
clean:
    mise exec -- dotnet clean Snaply.slnx
    -Remove-Item -Recurse -Force build/dist -ErrorAction SilentlyContinue
