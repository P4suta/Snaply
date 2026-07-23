#requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string]$ReleaseRoot,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$PreviousMsixBundle,

    [ValidateRange(0, 1000)]
    [int]$SoakIterations
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$release = (Resolve-Path $ReleaseRoot).Path
$uiTests = Join-Path (Split-Path $PSScriptRoot) 'src\Snaply.App\ui-tests.ps1'

function Stop-Snaply {
    param([System.Diagnostics.Process]$Process)

    if ($Process.HasExited) {
        return
    }

    $null = $Process.CloseMainWindow()
    if (-not $Process.WaitForExit(5000)) {
        Stop-Process -Id $Process.Id -Force
    }
}

function Invoke-Journey {
    param(
        [scriptblock]$Launch,
        [string]$Name,
        [int]$Soak
    )

    Get-Process Snaply -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Snaply $_
    }

    $started = [DateTime]::UtcNow
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    & $Launch
    $process = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $process = Get-Process Snaply -ErrorAction SilentlyContinue |
            Where-Object StartTime -ge $started |
            Sort-Object StartTime -Descending |
            Select-Object -First 1
        if (-not $process) {
            Start-Sleep -Milliseconds 100
        }
    } while (-not $process -and [DateTime]::UtcNow -lt $deadline)

    if (-not $process) {
        throw "$Name did not start."
    }

    try {
        winapp ui wait-for CaptureButton -a $process.Id -t 30000 | Out-Null
        $timer.Stop()
        if ($timer.Elapsed.TotalSeconds -gt 3) {
            throw "$Name cold start took $([Math]::Round($timer.Elapsed.TotalSeconds, 3))s."
        }

        $process.Refresh()
        $cpuBefore = $process.TotalProcessorTime
        Start-Sleep -Seconds 2
        $process.Refresh()
        $cpuPercent = (
            ($process.TotalProcessorTime - $cpuBefore).TotalMilliseconds /
            (2000 * [Environment]::ProcessorCount)) * 100
        if ($cpuPercent -ge 1) {
            throw "$Name idle CPU was $([Math]::Round($cpuPercent, 3))%."
        }

        & $uiTests -AppPid $process.Id -Architecture $Architecture -SoakIterations $Soak
        if ($LASTEXITCODE -ne 0) {
            throw "$Name UI tests failed."
        }

        $connections = @(Get-NetTCPConnection -OwningProcess $process.Id -ErrorAction SilentlyContinue)
        if ($connections.Count -ne 0) {
            throw "$Name opened $($connections.Count) TCP connection(s)."
        }
    }
    finally {
        Stop-Snaply $process
    }
}

$portable = Join-Path $release "portable\$Architecture\Snaply.exe"
if (-not (Test-Path -LiteralPath $portable)) {
    throw "Portable executable is missing: $portable"
}
Invoke-Journey { Start-Process -FilePath $portable | Out-Null } 'Portable' $SoakIterations

if ($PSVersionTable.PSEdition -eq 'Core') {
    Import-Module Appx -UseWindowsPowerShell
}

$bundles = @(Get-ChildItem (Join-Path $release 'msix') -Filter '*.msixbundle' -File)
if ($bundles.Count -ne 1) {
    throw "Expected one MSIX bundle, found $($bundles.Count)."
}
$bundle = $bundles[0]
Get-AppxPackage -Name Snaply -ErrorAction SilentlyContinue |
    Remove-AppxPackage -ErrorAction Stop

try {
    if ($PreviousMsixBundle) {
        Add-AppxPackage -Path (Resolve-Path $PreviousMsixBundle).Path
        Add-AppxPackage -Path $bundle.FullName -ForceUpdateFromAnyVersion
    }
    else {
        Add-AppxPackage -Path $bundle.FullName
    }

    $package = Get-AppxPackage -Name Snaply -ErrorAction Stop
    Invoke-Journey {
        Start-Process explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!App"
    } 'MSIX' 0
}
finally {
    Get-AppxPackage -Name Snaply -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction Stop
}
