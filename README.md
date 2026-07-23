# Snaply

Snaply is a Windows screenshot tool. It captures a region, a window, or the
entire virtual desktop, then places the capture on a randomized, image-derived
gradient background.

After each capture, Snaply opens a preview, copies a PNG to the clipboard, and
saves the same PNG to `Pictures\Screenshots\Snaply`.

## Usage

1. Open the Capture menu and choose Region, Window, or Desktop.
2. For Region, drag to select across one or more displays. For Window, pick
   from the system window picker.
3. In the preview, scroll to zoom, drag to pan, and double-tap to fit. Use
   Open Folder to open the save location.

## Install

Download the signed MSIX bundle or the self-contained x64/ARM64 portable ZIP
from [GitHub Releases](https://github.com/P4suta/Snaply/releases). Portable
builds require no .NET or Windows App SDK installation: extract the ZIP and run
`Snaply.exe`.

Snaply runs on Windows 11 24H2 or later on x64 and ARM64, in English, Japanese,
and Simplified Chinese.

## Privacy

Snaply runs entirely on the local machine. It has no telemetry, network access,
background service, tray process, global hotkey, or updater.

## License

Apache-2.0. See [LICENSE](LICENSE).
