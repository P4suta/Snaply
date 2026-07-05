using Snaply.Core.Models;

namespace Snaply.Core.Ports;

/// <summary>
/// Enumerates the display monitors available for full-screen capture. Implemented in the
/// Platform layer over the Win32 monitor list; the returned <see cref="MonitorInfo.Index"/>
/// values line up with <see cref="IScreenCaptureService.CaptureMonitorAsync"/>.
/// </summary>
public interface IMonitorEnumerationService
{
    /// <summary>The connected monitors, primary first (index 0 == primary).</summary>
    /// <returns>The monitors, ordered so index matches the capture service.</returns>
    IReadOnlyList<MonitorInfo> EnumerateMonitors();
}
