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
    /// first) so the picker can resolve the frontmost window under the cursor. Filtered
    /// to user-pickable windows (no tool windows, no untitled chrome).
    /// </summary>
    /// <returns>The pickable windows, topmost first.</returns>
    IReadOnlyList<WindowInfo> EnumerateTopLevelWindows();

    /// <summary>
    /// The current foreground window, or <c>null</c> if there is none. Not filtered like
    /// <see cref="EnumerateTopLevelWindows"/> — whatever the OS reports as foreground is
    /// returned so "capture the active window" always targets what the user is looking at.
    /// </summary>
    /// <returns>The foreground window, or <c>null</c>.</returns>
    WindowInfo? GetForegroundWindow();

    /// <summary>
    /// The <paramref name="target"/> window together with every visible window that shares its
    /// root owner — its owned dialogs (file pickers, modal dialogs), popups, menus and dropdowns.
    /// Unlike <see cref="EnumerateTopLevelWindows"/> this deliberately includes tool/untitled
    /// windows so a composite (region) capture of their combined bounds shows the window with its
    /// popups exactly as they appear on screen.
    /// </summary>
    /// <param name="target">Any window in the group (the owner or one of its popups).</param>
    /// <returns>The group's windows (including the owner); empty when the target is not found.</returns>
    IReadOnlyList<WindowInfo> EnumerateRelatedWindows(nint target);
}
