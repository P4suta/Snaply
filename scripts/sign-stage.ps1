#requires -Version 7
<#
.SYNOPSIS
    Collect Snaply's own PE files out of the built bundle into a flat staging dir
    (build/sign-stage/) for batch signing, and prepare an empty output dir
    (build/signed/). Uses the shared map in sign-map.ps1.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
. (Join-Path $PSScriptRoot 'sign-map.ps1')

$distRoot = Join-Path $root 'build/dist/Snaply'
$stageDir = Join-Path $root 'build/sign-stage'
$signedDir = Join-Path $root 'build/signed'

foreach ($d in @($stageDir, $signedDir)) {
    if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

foreach ($entry in $SnaplyFirstPartyPes.GetEnumerator()) {
    $src = Join-Path $distRoot $entry.Key
    if (-not (Test-Path $src)) { throw "expected first-party PE not found: $($entry.Key)" }
    Copy-Item $src (Join-Path $stageDir $entry.Value) -Force
    Write-Host "staged: $($entry.Key) -> $($entry.Value)"
}

Write-Host "==> Staged $($SnaplyFirstPartyPes.Count) file(s) into build/sign-stage"
