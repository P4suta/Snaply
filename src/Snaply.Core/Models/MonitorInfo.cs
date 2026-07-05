using Snaply.Core.Geometry;

namespace Snaply.Core.Models;

/// <summary>
/// A display monitor offered for full-screen capture. <see cref="Index"/> is the same
/// zero-based index accepted by <c>IScreenCaptureService.CaptureMonitorAsync</c> (0 == the
/// primary monitor); bounds are in physical, virtual-desktop pixels.
/// </summary>
/// <param name="Index">Zero-based monitor index (0 == primary).</param>
/// <param name="Bounds">The monitor's rectangle in physical virtual-desktop pixels.</param>
/// <param name="Dpi">The monitor's effective DPI.</param>
/// <param name="Primary">Whether this is the primary monitor.</param>
public sealed record MonitorInfo(int Index, PhysicalRect Bounds, Dpi Dpi, bool Primary);
