#requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$OutputRoot = 'build/repro'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$first = [System.IO.Path]::GetFullPath((Join-Path $root "$OutputRoot-a"))
$second = [System.IO.Path]::GetFullPath((Join-Path $root "$OutputRoot-b"))

function Assert-WorkspacePath {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
            $root + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the repository: $fullPath"
    }

    return $fullPath
}

function Get-RelativeWorkspacePath {
    param(
        [string]$BasePath,
        [string]$Path
    )

    $base = [System.IO.Path]::GetFullPath($BasePath).
        TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $base + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the payload: $fullPath"
    }

    return $fullPath.Substring($prefix.Length)
}

function Reset-BuildState {
    $sourceRoots = @(
        (Join-Path $root 'src'),
        (Join-Path $root 'tests')
    )
    $targets = Get-ChildItem -LiteralPath $sourceRoots -Directory -Recurse -Force |
        Where-Object Name -in @('bin', 'obj') |
        Sort-Object FullName -Descending

    foreach ($target in $targets) {
        $safePath = Assert-WorkspacePath $target.FullName
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
}

function Get-PayloadManifest {
    param([string]$BasePath)

    foreach ($relativeRoot in @(
            'portable/x64',
            'portable/arm64',
            'msix/inspect-x64',
            'msix/inspect-arm64')) {
        $path = Join-Path $BasePath $relativeRoot
        Get-ChildItem -LiteralPath $path -Recurse -File |
            Where-Object Name -notin @('AppxBlockMap.xml', '[Content_Types].xml') |
            ForEach-Object {
                $relative = Get-RelativeWorkspacePath $BasePath $_.FullName
                $relative = $relative.Replace('\', '/')
                "$relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            }
    }
}

$env:CI = 'true'
Reset-BuildState
& (Join-Path $PSScriptRoot 'release.ps1') `
    -Action Build `
    -Version $Version `
    -Publisher 'CN=Snaply' `
    -OutputRoot $first

Reset-BuildState
& (Join-Path $PSScriptRoot 'release.ps1') `
    -Action Build `
    -Version $Version `
    -Publisher 'CN=Snaply' `
    -OutputRoot $second

$firstManifest = @(Get-PayloadManifest $first | Sort-Object)
$secondManifest = @(Get-PayloadManifest $second | Sort-Object)
$difference = Compare-Object $firstManifest $secondManifest
if ($difference) {
    $difference | Format-Table | Out-String | Write-Host
    throw 'Unsigned release payloads are not reproducible.'
}

Write-Host "Reproducible payload entries: $($firstManifest.Count)"
