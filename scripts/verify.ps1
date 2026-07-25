#requires -Version 5.1
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'artifacts\verify'
$testOutput = Join-Path $root 'artifacts\verify-tests'

function Assert-BrandAssets {
    Add-Type -AssemblyName System.Drawing
    $expected = [ordered]@{
        'SplashScreen.scale-200.png' = @(1240, 600)
        'Square150x150Logo.scale-200.png' = @(300, 300)
        'Square44x44Logo.scale-200.png' = @(88, 88)
        'StoreLogo.png' = @(50, 50)
        'Wide310x150Logo.scale-200.png' = @(620, 300)
    }
    foreach ($size in @(16, 24, 32, 48, 256)) {
        foreach ($suffix in @('', '_altform-unplated', '_altform-lightunplated')) {
            $expected["Square44x44Logo.targetsize-$size$suffix.png"] = @($size, $size)
        }
    }

    foreach ($entry in $expected.GetEnumerator()) {
        $path = Join-Path $root "src\Snaply.App\Assets\$($entry.Key)"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required brand asset is missing: $($entry.Key)"
        }

        $bitmap = [System.Drawing.Bitmap]::new($path)
        try {
            if ($bitmap.Width -ne $entry.Value[0] -or
                $bitmap.Height -ne $entry.Value[1]) {
                throw "Brand asset has incorrect dimensions: $($entry.Key)"
            }

            $hasBrandColour = $false
            $hasTransparency = $false
            $stepX = [Math]::Max(1, [int][Math]::Floor($bitmap.Width / 64))
            $stepY = [Math]::Max(1, [int][Math]::Floor($bitmap.Height / 64))
            for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
                for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    $hasTransparency = $hasTransparency -or $pixel.A -eq 0
                    $hasBrandColour = $hasBrandColour -or (
                        $pixel.A -ge 128 -and
                        $pixel.R - $pixel.G -ge 80 -and
                        $pixel.R - $pixel.B -ge 80)
                }
            }

            if (-not $hasBrandColour -or -not $hasTransparency) {
                throw "Brand asset is not a transparent Snaply-colour image: $($entry.Key)"
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

Push-Location $root
try {
    Assert-BrandAssets
    if (Test-Path -LiteralPath $testOutput) {
        Remove-Item -LiteralPath $testOutput -Recurse -Force
    }

    Invoke-Checked dotnet @(
        'restore',
        'Snaply.slnx',
        '--locked-mode')
    Invoke-Checked dotnet @(
        'restore',
        'src/Snaply.App/Snaply.App.csproj',
        '--locked-mode',
        '-p:Configuration=Release')
    Invoke-Checked dotnet @(
        'format',
        'Snaply.slnx',
        '--verify-no-changes',
        '--no-restore')
    Invoke-Checked dotnet @(
        'test',
        'tests/Snaply.Tests/Snaply.Tests.csproj',
        '-c', $Configuration,
        '--no-restore',
        '-p:PathMap=',
        '--collect:XPlat Code Coverage',
        '--logger', 'trx;LogFileName=results.trx',
        '--results-directory', (Join-Path $testOutput 'imaging'))
    Invoke-Checked dotnet @(
        'test',
        'tests/Snaply.App.Tests/Snaply.App.Tests.csproj',
        '-c', $Configuration,
        '--no-restore',
        '--logger', 'trx;LogFileName=results.trx',
        '--results-directory', (Join-Path $testOutput 'app'))

    [xml]$imagingResults = Get-Content (
        Join-Path $testOutput 'imaging\results.trx') -Raw
    [xml]$appResults = Get-Content (
        Join-Path $testOutput 'app\results.trx') -Raw
    if ([int]$imagingResults.TestRun.ResultSummary.Counters.notExecuted -ne 0 -or
        [int]$appResults.TestRun.ResultSummary.Counters.notExecuted -ne 0) {
        throw 'Required tests were skipped.'
    }

    $coveragePath = Get-ChildItem (Join-Path $testOutput 'imaging') `
        -Recurse -Filter coverage.cobertura.xml -File |
        Select-Object -First 1
    if (-not $coveragePath) {
        throw 'Coverage report was not produced.'
    }

    [xml]$coverage = Get-Content $coveragePath.FullName -Raw
    if ([double]$coverage.coverage.'line-rate' -lt 0.90 -or
        [double]$coverage.coverage.'branch-rate' -lt 0.85) {
        throw 'Imaging coverage is below 90% line or 85% branch.'
    }

    if (-not $SkipPublish) {
        if (Test-Path -LiteralPath $output) {
            Remove-Item -LiteralPath $output -Recurse -Force
        }

        foreach ($architecture in @(
                [pscustomobject]@{ Platform = 'x64'; Rid = 'win-x64'; Name = 'x64' },
                [pscustomobject]@{ Platform = 'ARM64'; Rid = 'win-arm64'; Name = 'arm64' })) {
            Invoke-Checked dotnet @(
                'publish',
                'src/Snaply.App/Snaply.App.csproj',
                '-c', $Configuration,
                '-r', $architecture.Rid,
                "-p:Platform=$($architecture.Platform)",
                '-p:WindowsPackageType=None',
                '--self-contained', 'true',
                '--no-restore',
                '-o', (Join-Path $output $architecture.Name))
        }
    }
}
finally {
    Pop-Location
}
