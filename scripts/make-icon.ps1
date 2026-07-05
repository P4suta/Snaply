#requires -Version 5
<#
.SYNOPSIS
    Generate a multi-resolution Windows .ico from a source PNG.

.DESCRIPTION
    Resizes the source PNG to standard icon sizes (16..256) with high-quality bicubic scaling,
    encodes each as PNG, and packs them into a single .ico (PNG-compressed entries, which Windows
    supports). Run once when the app art changes:
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/make-icon.ps1
    Uses Windows PowerShell (System.Drawing) — no ImageMagick dependency.
#>
param(
    [string]$Source = "$PSScriptRoot\..\src\Snaply.App\Assets\AppIcon.png",
    [string]$Out = "$PSScriptRoot\..\src\Snaply.App\Assets\AppIcon.ico"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source).Path)
$pngs = @()
try {
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($src, 0, 0, $s, $s)
        }
        finally { $g.Dispose() }
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngs += , ($ms.ToArray())
        $ms.Dispose()
    }
}
finally { $src.Dispose() }

$dir = Split-Path $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$fs = [System.IO.File]::Create((Join-Path (Resolve-Path $dir).Path (Split-Path $Out -Leaf)))
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)             # reserved
    $bw.Write([uint16]1)             # type = icon
    $bw.Write([uint16]$sizes.Count)  # image count
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $data = $pngs[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }  # 0 encodes 256 in the directory entry
        $bw.Write([byte]$dim)         # width
        $bw.Write([byte]$dim)         # height
        $bw.Write([byte]0)            # palette count
        $bw.Write([byte]0)            # reserved
        $bw.Write([uint16]1)          # colour planes
        $bw.Write([uint16]32)         # bits per pixel
        $bw.Write([uint32]$data.Length)
        $bw.Write([uint32]$offset)
        $offset += $data.Length
    }
    foreach ($data in $pngs) { $bw.Write($data) }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Host "Wrote $Out ($($sizes.Count) sizes)"
