#requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [ValidateSet('Build', 'Collect', 'Verify', 'Package')]
    [string]$Action,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.1',

    [string]$Publisher = 'CN=Snaply',

    [string]$SignerSubjectContains,

    [string]$OutputRoot = 'build/release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $root $OutputRoot))
}

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

function Reset-Directory {
    param([string]$Path)

    $safePath = Assert-WorkspacePath $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [switch]$Quiet
    )

    if ($Quiet) {
        & $FilePath @Arguments | Out-Null
    }
    else {
        & $FilePath @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Get-PeMachine {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        $stream.Position = 0x3c
        $stream.Position = $reader.ReadInt32() + 4
        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-Portable {
    param(
        [string]$Path,
        [uint16]$ExpectedMachine
    )

    foreach ($required in @(
            'Snaply.exe',
            'Snaply.dll',
            'Snaply.Imaging.dll',
            'Snaply.runtimeconfig.json',
            'Snaply.deps.json',
            'LICENSE',
            'NOTICE',
            'dependency-licenses.txt',
            'Assets\AppIcon.ico',
            'Assets\AppIcon.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $required))) {
            throw "Portable payload is missing $required"
        }
    }

    if (@(Get-ChildItem -LiteralPath $Path -Recurse -Filter '*.xbf').Count -eq 0) {
        throw 'Portable payload has no compiled XAML.'
    }

    if (@(Get-ChildItem -LiteralPath $Path -Filter '*.pri').Count -eq 0) {
        throw 'Portable payload has no PRI resource index.'
    }

    $runtimeOptions = (Get-Content (Join-Path $Path 'Snaply.runtimeconfig.json') -Raw |
        ConvertFrom-Json).runtimeOptions
    if (-not $runtimeOptions.includedFrameworks) {
        throw 'Portable payload is framework-dependent.'
    }

    if ((Get-PeMachine (Join-Path $Path 'Snaply.exe')) -ne $ExpectedMachine) {
        throw 'Portable apphost architecture is incorrect.'
    }
}

function Get-WindowsSdkTool {
    param([string]$Name)

    $packages = if ($env:NUGET_PACKAGES) {
        $env:NUGET_PACKAGES
    }
    else {
        Join-Path $env:USERPROFILE '.nuget\packages'
    }

    [xml]$project = Get-Content (Join-Path $root 'src\Snaply.App\Snaply.App.csproj') -Raw
    $buildTools = $project.SelectSingleNode(
        "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference'][@Include='Microsoft.Windows.SDK.BuildTools']")
    if (-not $buildTools) {
        throw 'Microsoft.Windows.SDK.BuildTools is not pinned.'
    }

    $package = Join-Path $packages (
        "microsoft.windows.sdk.buildtools\$($buildTools.Version)")
    $tool = Get-ChildItem $package `
        -Recurse -Filter $Name -File |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object FullName |
        Select-Object -Last 1
    if (-not $tool) {
        throw "$Name was not found in Microsoft.Windows.SDK.BuildTools."
    }

    return $tool.FullName
}

function Write-StampedManifest {
    param([string]$Path)

    [xml]$manifest = Get-Content (Join-Path $root 'src\Snaply.App\Package.appxmanifest') -Raw
    $manifest.Package.Identity.Version = "$Version.0"
    $manifest.Package.Identity.Publisher = $Publisher

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $manifest.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Get-ProductionPackages {
    $packages = @{}
    foreach ($assetsPath in @(
            (Join-Path $root 'src\Snaply.App\obj\project.assets.json'),
            (Join-Path $root 'src\Snaply.Imaging\obj\project.assets.json'))) {
        $assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
        $packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package') {
                continue
            }

            $id, $packageVersion = $library.Name -split '/', 2
            $packages[$library.Name] = [pscustomobject]@{
                Id = $id
                Version = $packageVersion
                Root = Join-Path $packageRoot "$($id.ToLowerInvariant())\$packageVersion"
            }
        }
    }

    return $packages.Values | Sort-Object Id, Version
}

function Write-LicenseInventory {
    param([string]$Path)

    $knownMicrosoftLicenses = @(
        'Microsoft.Graphics.Win2D',
        'Microsoft.Web.WebView2',
        'Microsoft.Windows.SDK.BuildTools',
        'Microsoft.Windows.SDK.BuildTools.MSIX',
        'Microsoft.WindowsAppSDK.Base',
        'Microsoft.WindowsAppSDK.Foundation',
        'Microsoft.WindowsAppSDK.InteractiveExperiences',
        'Microsoft.WindowsAppSDK.WinUI'
    )
    $allowedExpressions = @('Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'MIT')
    $lines = [System.Collections.Generic.List[string]]::new()

    foreach ($package in Get-ProductionPackages) {
        $nuspec = Get-ChildItem -LiteralPath $package.Root -Filter '*.nuspec' -File |
            Select-Object -First 1
        if (-not $nuspec) {
            throw "No nuspec found for $($package.Id) $($package.Version)."
        }

        [xml]$metadata = Get-Content $nuspec.FullName -Raw
        $licenseNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        $licenseUrlNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
        $license = $null

        if ($licenseNode -and $licenseNode.type -eq 'expression') {
            $license = $licenseNode.InnerText.Trim()
            if ($license -notin $allowedExpressions) {
                throw "Unapproved license '$license' for $($package.Id)."
            }
        }
        elseif ($licenseNode -and $licenseNode.type -eq 'file') {
            $licenseFile = Join-Path $package.Root $licenseNode.InnerText.Trim()
            if (-not (Test-Path -LiteralPath $licenseFile)) {
                throw "License file is missing for $($package.Id)."
            }

            if ($package.Id -notin $knownMicrosoftLicenses) {
                throw "Unreviewed file license for $($package.Id)."
            }

            $license = "file:$($licenseNode.InnerText.Trim())"
        }
        elseif ($licenseUrlNode -and $package.Id -in $knownMicrosoftLicenses) {
            $license = "url:$($licenseUrlNode.InnerText.Trim())"
        }
        else {
            throw "Unknown license for $($package.Id) $($package.Version)."
        }

        $lines.Add("$($package.Id) $($package.Version) — $license")
    }

    [System.IO.File]::WriteAllLines($Path, $lines, [System.Text.UTF8Encoding]::new($false))
}

function Assert-Msix {
    param(
        [string]$Path,
        [string]$ExpectedArchitecture,
        [string]$MakeAppx,
        [string]$Scratch
    )

    Reset-Directory $Scratch
    Invoke-Checked -FilePath $MakeAppx `
        -Arguments @('unpack', '/p', $Path, '/d', $Scratch, '/o') `
        -Quiet
    [xml]$manifest = Get-Content (Join-Path $Scratch 'AppxManifest.xml') -Raw

    $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($identity.Version -ne "$Version.0" -or
        $identity.Publisher -ne $Publisher -or
        $identity.ProcessorArchitecture -ne $ExpectedArchitecture) {
        throw "MSIX identity mismatch for $ExpectedArchitecture."
    }

    $capabilities = @($manifest.SelectNodes("//*[local-name()='Capability']") |
        ForEach-Object { $_.Name })
    if ($capabilities.Count -ne 1 -or $capabilities[0] -ne 'runFullTrust') {
        throw "Unexpected MSIX capabilities: $($capabilities -join ', ')"
    }

    $targetFamily = $manifest.SelectSingleNode("//*[local-name()='TargetDeviceFamily']")
    if ($targetFamily.MinVersion -ne '10.0.26100.0') {
        throw 'MSIX minimum Windows version is incorrect.'
    }
}

function Copy-SigningStage {
    $stage = Join-Path $output 'sign-stage'
    Reset-Directory $stage
    $portableStage = Join-Path $stage 'portable'
    $msixStage = Join-Path $stage 'msix'
    New-Item -ItemType Directory -Path $portableStage, $msixStage -Force | Out-Null

    foreach ($architecture in @('x64', 'arm64')) {
        $payload = Join-Path $output "portable\$architecture"
        foreach ($file in @('Snaply.exe', 'Snaply.dll', 'Snaply.Imaging.dll')) {
            Copy-Item (Join-Path $payload $file) `
                (Join-Path $portableStage "$architecture-$file")
        }
    }

    Copy-Item (Join-Path $output "msix\Snaply-$Version.msixbundle") $msixStage
}

function Build-Release {
    Reset-Directory $output
    Push-Location $root
    try {
        Invoke-Checked 'dotnet' @(
            'restore',
            'src/Snaply.App/Snaply.App.csproj',
            '--locked-mode')
        $licenseInventory = Join-Path $output 'dependency-licenses.txt'
        Write-LicenseInventory $licenseInventory

        $architectures = @(
            [pscustomobject]@{ Name = 'x64'; Platform = 'x64'; Rid = 'win-x64'; Machine = [uint16]0x8664 },
            [pscustomobject]@{ Name = 'arm64'; Platform = 'ARM64'; Rid = 'win-arm64'; Machine = [uint16]0xaa64 }
        )

        foreach ($architecture in $architectures) {
            $portable = Join-Path $output "portable\$($architecture.Name)"
            New-Item -ItemType Directory -Path $portable -Force | Out-Null
            Invoke-Checked 'dotnet' @(
                'publish',
                'src/Snaply.App/Snaply.App.csproj',
                '-c', 'Release',
                '-r', $architecture.Rid,
                "-p:Platform=$($architecture.Platform)",
                "-p:Version=$Version",
                '-p:WindowsPackageType=None',
                '-p:SelfContained=true',
                '-p:WindowsAppSDKSelfContained=true',
                '-p:PublishReadyToRun=true',
                '-o', $portable,
                '--no-restore')

            Copy-Item (Join-Path $root 'LICENSE') $portable
            Copy-Item (Join-Path $root 'NOTICE') $portable
            Copy-Item $licenseInventory $portable
            Assert-Portable $portable $architecture.Machine
        }

        $msix = Join-Path $output 'msix'
        $manifest = Join-Path $msix 'Package.appxmanifest'
        New-Item -ItemType Directory -Path $msix -Force | Out-Null
        Write-StampedManifest $manifest
        $makeAppx = Get-WindowsSdkTool 'makeappx.exe'
        $bundleInput = Join-Path $msix 'input'
        New-Item -ItemType Directory -Path $bundleInput -Force | Out-Null

        foreach ($architecture in $architectures) {
            $packageOutput = Join-Path $msix "package-$($architecture.Name)"
            New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
            Invoke-Checked 'dotnet' @(
                'build',
                'src/Snaply.App/Snaply.App.csproj',
                '-c', 'Release',
                "-p:Platform=$($architecture.Platform)",
                "-p:RuntimeIdentifier=$($architecture.Rid)",
                "-p:Version=$Version",
                '-p:WindowsPackageType=MSIX',
                "-p:SnaplyPackageManifest=$manifest",
                '-p:GenerateAppxPackageOnBuild=true',
                '-p:AppxPackageSigningEnabled=false',
                '-p:AppxBundle=Never',
                '-p:UapAppxPackageBuildMode=SideLoadOnly',
                "-p:AppxPackageDir=$packageOutput\",
                '--no-restore')

            $packages = @(Get-ChildItem -LiteralPath $packageOutput -Recurse -Filter '*.msix' -File)
            if ($packages.Count -ne 1) {
                throw "Expected one $($architecture.Name) MSIX, found $($packages.Count)."
            }

            $target = Join-Path $bundleInput "Snaply-$($architecture.Name).msix"
            Copy-Item $packages[0].FullName $target
            Assert-Msix $target $architecture.Name $makeAppx `
                (Join-Path $msix "inspect-$($architecture.Name)")
        }

        $bundle = Join-Path $msix "Snaply-$Version.msixbundle"
        Invoke-Checked -FilePath $makeAppx `
            -Arguments @('bundle', '/d', $bundleInput, '/p', $bundle, '/o') `
            -Quiet
        Copy-SigningStage
    }
    finally {
        Pop-Location
    }
}

function Find-SignedFile {
    param(
        [string]$Directory,
        [string]$Name
    )

    $matches = @(Get-ChildItem -LiteralPath $Directory -Recurse -File |
        Where-Object Name -eq $Name)
    if ($matches.Count -ne 1) {
        throw "Expected one signed $Name, found $($matches.Count)."
    }

    return $matches[0].FullName
}

function Collect-Signed {
    $signed = Join-Path $output 'signed'
    foreach ($architecture in @('x64', 'arm64')) {
        foreach ($file in @('Snaply.exe', 'Snaply.dll', 'Snaply.Imaging.dll')) {
            $stageName = "$architecture-$file"
            Copy-Item (Find-SignedFile (Join-Path $signed 'portable') $stageName) `
                (Join-Path $output "portable\$architecture\$file") -Force
        }
    }

    Copy-Item (Find-SignedFile (Join-Path $signed 'msix') "Snaply-$Version.msixbundle") `
        (Join-Path $output "msix\Snaply-$Version.msixbundle") -Force
}

function Assert-Signature {
    param(
        [string]$Path,
        [string]$SignTool
    )

    Invoke-Checked $SignTool @('verify', '/pa', '/all', '/tw', $Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Subject -notlike "*$SignerSubjectContains*") {
        throw "Unexpected Authenticode signer for $Path."
    }
}

function Verify-Release {
    if ([string]::IsNullOrWhiteSpace($SignerSubjectContains)) {
        throw 'SignerSubjectContains is required for verification.'
    }

    $signTool = Get-WindowsSdkTool 'signtool.exe'
    foreach ($architecture in @('x64', 'arm64')) {
        foreach ($file in @('Snaply.exe', 'Snaply.dll', 'Snaply.Imaging.dll')) {
            Assert-Signature (Join-Path $output "portable\$architecture\$file") $signTool
        }
    }

    Assert-Signature (Join-Path $output "msix\Snaply-$Version.msixbundle") $signTool
}

function New-DeterministicZip {
    param(
        [string]$Source,
        [string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open(
        $Destination,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File |
                Sort-Object FullName) {
                $relative = (Get-RelativeWorkspacePath $Source $file.FullName).
                    Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $relative,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [System.DateTimeOffset]::new(
                    1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
                $input = $file.OpenRead()
                $outputStream = $entry.Open()
                try {
                    $input.CopyTo($outputStream)
                }
                finally {
                    $outputStream.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Package-Release {
    $package = Join-Path $output 'package'
    Reset-Directory $package
    foreach ($architecture in @('x64', 'arm64')) {
        New-DeterministicZip (Join-Path $output "portable\$architecture") `
            (Join-Path $package "snaply-v$Version-win-$architecture.zip")
    }

    Copy-Item (Join-Path $output "msix\Snaply-$Version.msixbundle") `
        (Join-Path $package "snaply-v$Version.msixbundle")
    Copy-Item (Join-Path $root 'LICENSE') $package
    Copy-Item (Join-Path $root 'NOTICE') $package
    Copy-Item (Join-Path $output 'dependency-licenses.txt') $package

    $sbom = Join-Path $output '_manifest\spdx_2.2\manifest.spdx.json'
    if (-not (Test-Path -LiteralPath $sbom)) {
        throw 'SPDX SBOM is missing.'
    }
    Copy-Item $sbom (Join-Path $package 'snaply.spdx.json')

    $hashes = Get-ChildItem -LiteralPath $package -File |
        Where-Object Name -ne 'SHA256SUMS.txt' |
        Sort-Object Name |
        ForEach-Object {
            '{0}  {1}' -f
                (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),
                $_.Name
        }
    [System.IO.File]::WriteAllLines(
        (Join-Path $package 'SHA256SUMS.txt'),
        $hashes,
        [System.Text.Encoding]::ASCII)
}

switch ($Action) {
    'Build' { Build-Release }
    'Collect' { Collect-Signed }
    'Verify' { Verify-Release }
    'Package' { Package-Release }
}
