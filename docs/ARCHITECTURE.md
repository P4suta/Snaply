# Architecture

Snaply is built with **ports & adapters (hexagonal) clean architecture**. The
single most important quality bar in this project is architectural cleanliness:
boundaries, the direction of dependencies, and testability come before feature
count.

## Layers

```
Snaply.App        WinUI 3 shell · DI composition root · ViewModels · XAML views
   │ depends on
   ▼
Snaply.Platform   Adapters: WGC capture · Win2D compositing · PNG/clipboard ·
   │ depends on   Win32 hotkeys · tray
   ▼
Snaply.Core       Zero dependencies · immutable records · pure logic ·
                  Result-typed failures · ports (interfaces)
```

The dependency arrow always points inward. `Snaply.Core` never references a
platform type.

### Snaply.Core

Targets plain `net10.0` (no Windows TFM) and has **zero package dependencies**
beyond the solution-wide analyzers. It holds:

- Immutable `record` domain models.
- Pure logic (capture pipeline orchestration, beautify defaults, geometry).
- **Ports** — the interfaces the outer layers implement
  (`IScreenCaptureService`, `IWindowEnumerationService`, …).
- `Result` / `Error` types for explicit, non-throwing failure.

Because Core is platform-independent, its unit tests run on Linux in CI — the
architecture fitness test made executable.

### Snaply.Platform

The adapters that implement Core's ports against real Windows APIs: Windows
Graphics Capture, Win2D compositing, PNG encode, clipboard, Win32 global
hotkeys, and the system tray. This is the only layer allowed to touch WinRT /
Win32 / Win2D.

### Snaply.App

The WinUI 3 shell and the **DI composition root**. It wires the ports to their
Platform adapters and hosts the ViewModels and XAML. ViewModels do not reference
WinUI types directly — localized strings arrive via `IUiStrings`, etc.

## Rules of thumb

- Pure functions live in Core; every side effect is isolated at the outermost
  edge.
- Failures are explicit via `Result` — no silent catches.
- One service, one responsibility. No god objects.
- Modern C# discipline is mechanized by analyzers (`TreatWarningsAsErrors`).

When a design decision is ambiguous, choose beauty, separation, and testability.
