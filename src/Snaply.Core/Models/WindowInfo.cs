using Snaply.Core.Geometry;

namespace Snaply.Core.Models;

/// <summary>
/// A top-level window offered to the user for window-selection capture. The handle
/// is an opaque native window handle (HWND) the Platform layer resolves back; the
/// domain treats it as an identity only. Bounds are the DWM extended frame in
/// physical, virtual-desktop pixels — the same coordinate space the capture and
/// the picker overlay work in.
/// </summary>
/// <param name="Handle">The native window handle (HWND) as an opaque identity.</param>
/// <param name="Title">The window's title-bar text.</param>
/// <param name="Bounds">The window's frame in physical virtual-desktop pixels.</param>
public sealed record WindowInfo(nint Handle, string Title, PhysicalRect Bounds);
