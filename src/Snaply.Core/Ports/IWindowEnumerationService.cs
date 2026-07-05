using Snaply.Core.Models;

namespace Snaply.Core.Ports;

/// <summary>
/// Enumerates the user-pickable top-level windows for window-selection capture.
/// Implemented in the Platform layer over the Win32 window list; the picker overlay
/// hit-tests the cursor against the returned <see cref="WindowInfo.Bounds"/>.
/// </summary>
public interface IWindowEnumerationService
{
    /// <summary>
    /// The visible, non-minimized top-level windows, ordered front-to-back (topmost
    /// first) so the picker can resolve the frontmost window under the cursor.
    /// </summary>
    /// <returns>The pickable windows, topmost first.</returns>
    IReadOnlyList<WindowInfo> EnumerateTopLevelWindows();
}
