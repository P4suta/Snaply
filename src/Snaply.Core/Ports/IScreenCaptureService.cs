using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Core.Ports;

/// <summary>
/// Captures pixels from the screen at full physical resolution. Implemented in the
/// Platform layer over Windows.Graphics.Capture; the domain only ever sees the
/// resulting <see cref="CapturedImage"/>.
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>Capture a physical-pixel region of the virtual desktop.</summary>
    /// <param name="region">The region to capture, in physical pixels.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <returns>The captured image, or a failure.</returns>
    Task<Result<CapturedImage>> CaptureRegionAsync(PhysicalRect region, CancellationToken cancellationToken = default);

    /// <summary>Capture an entire monitor.</summary>
    /// <param name="monitorIndex">Zero-based index of the monitor to capture.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <returns>The captured image, or a failure.</returns>
    Task<Result<CapturedImage>> CaptureMonitorAsync(int monitorIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Capture a single top-level window. Renders the window itself via PrintWindow for a
    /// clean soft edge, automatically falling back to a WGC grab (with a one-pixel edge trim)
    /// for the occasional window that PrintWindow renders black/empty.
    /// </summary>
    /// <param name="windowHandle">The native window handle (HWND) to capture.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <returns>The captured image, or a failure.</returns>
    Task<Result<CapturedImage>> CaptureWindowAsync(nint windowHandle, CancellationToken cancellationToken = default);
}
