#requires -Version 7
<#
.SYNOPSIS
    Assemble the self-bundled Snaply distributable with an obvious entry point.

.DESCRIPTION
    Produces build/dist/Snaply with the find-my-files layout:
        Snaply.exe   - tiny Native AOT launcher (the one thing to double-click)
        README.txt   - how to start
        app/         - the real self-contained WinUI app + its ~200 runtime files
    The apphost and its DLLs must stay together in app/ (the apphost resolves its
    *.deps.json / DLLs from its own directory); the launcher starts app/Snaply.App.exe
    with WorkingDirectory=app/ and exits.

.PARAMETER Version
    Optional release version tag (e.g. v0.1.0). When given, the shipped binaries
    are stamped with this version and -Package names the zip
    snaply-<version>-win-x64.zip. Omit for a plain dev bundle.

.PARAMETER Package
    Also zip the bundle contents into build/package/ + SHA256SUMS.txt.

.PARAMETER SkipBuild
    Skip assembling the bundle and operate on the existing build/dist/Snaply
    (verify + package only). Used by the release pipeline to zip the *signed*
    bundle without rebuilding it (which would discard the signatures).
#>
param(
    [string]$Version,
    [switch]$Package,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

$distRoot    = Join-Path $root 'build/dist/Snaply'
$appDir      = Join-Path $distRoot 'app'
$launcherOut = Join-Path $root 'build/.launcher'

# A release version stamps the assemblies; a bare number (no leading 'v') is what
# MSBuild's Version property expects.
$versionArgs = @()
if ($Version) {
    if ($Version -notmatch '^v?[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Version must look like v1.2.3 (got '$Version')"
    }
    $bare = $Version.TrimStart('v')
    $versionArgs = @("-p:Version=$bare")
    Write-Host "==> Release build, version $bare"
}

# Native AOT codegen needs the MSVC linker; the ILCompiler locates it via vswhere.exe.
# The mise shell doesn't carry the VS dev environment, so put the VS Installer dir (where
# vswhere lives) on PATH — that's enough for the ILCompiler to find and set up the toolchain.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $vsInstaller 'vswhere.exe')) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

if ($SkipBuild) {
    Write-Host '==> SkipBuild: packaging the existing bundle (no rebuild)'
    if (-not (Test-Path $distRoot)) { throw "SkipBuild set but no bundle at $distRoot" }
}
else {

Write-Host '==> Cleaning previous bundle'
if (Test-Path $distRoot) { Remove-Item -Recurse -Force $distRoot }

Write-Host '==> Publishing app (self-contained) into app/'
dotnet publish src/Snaply.App/Snaply.App.csproj -c Release -r win-x64 -p:Platform=x64 -o $appDir @versionArgs
if ($LASTEXITCODE -ne 0) { throw "app publish failed ($LASTEXITCODE)" }

# The scriptable CLI + MCP host (snaply.exe) ships in the same app/ folder: it shares the
# Windows App SDK runtime and the Core/Application/Platform DLLs already there, and carries
# its own snaply.deps.json / runtimeconfig, so the two apphosts coexist without conflict.
Write-Host '==> Publishing CLI (self-contained) into app/'
dotnet publish src/Snaply.Cli/Snaply.Cli.csproj -c Release -r win-x64 -p:Platform=x64 -o $appDir @versionArgs
if ($LASTEXITCODE -ne 0) { throw "cli publish failed ($LASTEXITCODE)" }

Write-Host '==> Publishing launcher (Native AOT)'
dotnet publish src/Snaply.Launcher/Snaply.Launcher.csproj -c Release -r win-x64 -p:Platform=x64 -o $launcherOut @versionArgs
if ($LASTEXITCODE -ne 0) { throw "launcher publish failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $launcherOut 'Snaply.Launcher.exe') (Join-Path $distRoot 'Snaply.exe') -Force

Write-Host '==> Pruning unused Windows App SDK locales'
# Keep the locales Snaply ships (en / ja / zh-Hans). The Windows App SDK names its
# own translation folders 'zh-cn' (not 'zh-hans'), so keep that too for the system
# chrome; the app's own strings live merged in Snaply.App.pri and are not pruned here.
$keep = @('en-us', 'ja-jp', 'zh-cn', 'zh-hans')
$localeRegex = [regex]'(?i)^[a-z]{2,3}(-[A-Za-z0-9]+){1,3}$'
$prune = @(Get-ChildItem -Path $appDir -Directory |
    Where-Object { $localeRegex.IsMatch($_.Name) -and ($keep -notcontains $_.Name.ToLowerInvariant()) })
foreach ($dir in $prune) { Remove-Item -Recurse -Force $dir.FullName }
Write-Host "    pruned $($prune.Count) locale folder(s)"

Write-Host '==> Writing README.txt'
$readme = @'
Snaply
======

To start: double-click  Snaply.exe  (in this folder).

The "app" folder holds the application and its bundled runtime — keep it next to
Snaply.exe. Nothing needs to be installed; this build is self-contained.

Command line & AI: app\snaply.exe is the scriptable CLI and MCP server
(e.g.  app\snaply.exe capture full --out shot.png  or  app\snaply.exe mcp serve).
'@
Set-Content -Path (Join-Path $distRoot 'README.txt') -Value $readme -Encoding UTF8

} # end if (-not $SkipBuild)

Write-Host '==> Self-verifying bundle'
$required = @((Join-Path $distRoot 'Snaply.exe'), (Join-Path $appDir 'Snaply.App.exe'), (Join-Path $appDir 'snaply.exe'))
$missing = @($required | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) { throw "bundle is missing: $($missing -join ', ')" }

# Guard against a framework-dependent publish. A self-contained app's runtimeconfig
# carries `includedFrameworks` (the .NET runtime travels inside the bundle); a
# framework-dependent one carries `framework` and dies with "You must install or
# update .NET to run this application" on any machine without the SDK. This shipped
# once in v0.1.0 (Snaply.App was missing <SelfContained>true</SelfContained> — only
# WindowsAppSDKSelfContained was set, which bundles WinAppSDK but not the runtime).
# Fail the publish here so it can never ship again.
foreach ($exe in @('Snaply.App', 'snaply')) {
    $rc = Join-Path $appDir "$exe.runtimeconfig.json"
    if (-not (Test-Path $rc)) { throw "self-contained check: missing $rc" }
    $opts = (Get-Content $rc -Raw | ConvertFrom-Json).runtimeOptions
    if (-not $opts.includedFrameworks) {
        throw "self-contained check FAILED: $exe.exe is framework-dependent (runtimeconfig has no 'includedFrameworks'). Set <SelfContained>true</SelfContained> in its csproj; otherwise the bundle errors with 'You must install or update .NET' on a clean machine."
    }
}
Write-Host '    self-contained: Snaply.App + CLI carry their own .NET runtime'

# Shipping-integrity checks beyond mere existence — each catches a way the bundle can be
# subtly broken yet still "have the exe". SkipBuild runs these too, so a signed bundle is
# re-gated before packaging.
# 1) Compiled WinUI XAML: the app .pri + at least one .xbf must reach publish, or the first
#    window dies with "XAML parsing failed" (see the PublishWinUIXamlArtifacts target).
if (-not (Test-Path (Join-Path $appDir 'Snaply.App.pri'))) { throw "bundle integrity: app/Snaply.App.pri missing (compiled WinUI resources absent -> XAML parse failure at first window)" }
if (@(Get-ChildItem -Path $appDir -Recurse -Filter '*.xbf').Count -eq 0) { throw "bundle integrity: no compiled XAML (*.xbf) under app/" }
# 2) Runtime app icon: AppWindow.SetIcon loads these by real file path, not embedded resource.
foreach ($icon in @('Assets/AppIcon.ico', 'Assets/AppIcon.png')) {
    if (-not (Test-Path (Join-Path $appDir $icon))) { throw "bundle integrity: app/$icon missing (taskbar/title-bar icon won't load at runtime)" }
}
# 3) The CLI's networked MCP transport (mcp serve --transport http) hosts ASP.NET Core; if the
#    shared framework didn't self-contain, `mcp serve` dies at runtime. Spot-check representatives.
foreach ($dll in @('Microsoft.AspNetCore.dll', 'ModelContextProtocol.AspNetCore.dll')) {
    if (-not (Test-Path (Join-Path $appDir $dll))) { throw "bundle integrity: app/$dll missing (self-contained ASP.NET Core / MCP HTTP transport incomplete)" }
}
# 4) Architecture: every first-party exe must be an x64 (AMD64=0x8664) PE — a stray AnyCPU/x86
#    apphost would fail to load the win-x64 runtime.
foreach ($exe in @((Join-Path $distRoot 'Snaply.exe'), (Join-Path $appDir 'Snaply.App.exe'), (Join-Path $appDir 'snaply.exe'))) {
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x8664) { throw "bundle integrity: $([System.IO.Path]::GetFileName($exe)) is not an x64 PE (machine=0x{0:X4})" -f $machine }
}
Write-Host '    integrity: WinUI .pri/.xbf, app icon, ASP.NET Core/MCP, x64 apphosts all present'

Write-Host "==> Bundle ready: $distRoot"

if ($Package) {
    $pkgDir = Join-Path $root 'build/package'
    New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null
    # Release zips are named snaply-vX.Y.Z-win-x64.zip; a dev bundle is plain.
    $zipName = if ($Version) { "snaply-v$($Version.TrimStart('v'))-win-x64.zip" } else { 'snaply-win-x64.zip' }
    $zip = Join-Path $pkgDir $zipName
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Write-Host "==> Zipping bundle -> $zipName"
    # Archive the Snaply/ folder itself (not just its contents), so extracting the zip drops a
    # single self-contained `Snaply\` folder rather than scattering Snaply.exe / README.txt /
    # app\ loose into the user's Downloads.
    Compress-Archive -Path $distRoot -DestinationPath $zip

    # Checksum every attached release asset, not just the zip: if the SBOM was
    # generated (build/sbom/, present in the release job), include it so every
    # download has a manual-verification path, not only the zip.
    $sumLines = @()
    $sumLines += "{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant(), $zipName
    $sbom = Join-Path $root 'build/sbom/snaply.cdx.json'
    if (Test-Path $sbom) {
        $sumLines += "{0}  {1}" -f (Get-FileHash $sbom -Algorithm SHA256).Hash.ToLowerInvariant(), 'snaply.cdx.json'
    }
    Set-Content -Path (Join-Path $pkgDir 'SHA256SUMS.txt') -Value $sumLines -Encoding ascii
    Write-Host "==> Package: $zip"
}
