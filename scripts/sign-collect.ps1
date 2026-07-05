#requires -Version 7
<#
.SYNOPSIS
    Map the signed PE files in build/signed/ back over the bundle, using the same
    map (reverse direction) as sign-stage.ps1.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
. (Join-Path $PSScriptRoot 'sign-map.ps1')

$distRoot = Join-Path $root 'build/dist/Snaply'
$signedDir = Join-Path $root 'build/signed'

foreach ($entry in $SnaplyFirstPartyPes.GetEnumerator()) {
    $signed = Join-Path $signedDir $entry.Value
    if (-not (Test-Path $signed)) { throw "signed file missing (signing did not run?): $($entry.Value)" }
    Copy-Item $signed (Join-Path $distRoot $entry.Key) -Force
    Write-Host "collected: $($entry.Value) -> $($entry.Key)"
}

Write-Host "==> Copied $($SnaplyFirstPartyPes.Count) signed file(s) back into the bundle"
