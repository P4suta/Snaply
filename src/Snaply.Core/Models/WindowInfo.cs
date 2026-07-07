using Snaply.Core.Geometry;

namespace Snaply.Core.Models;

/// <summary>
/// A top-level window offered for window-selection capture. The handle is an opaque native
/// window handle (HWND) the Platform layer resolves back; the domain treats it as an identity
/// only. Bounds are the DWM extended frame in physical, virtual-desktop pixels — the same
/// coordinate space the capture and the picker overlay work in. The remaining fields let AI
/// clients (and the CLI) target a window precisely — by process, class, or foreground state —
/// and let group capture follow owner relationships to a window's popups/dialogs.
/// </summary>
/// <param name="Handle">The native window handle (HWND) as an opaque identity.</param>
/// <param name="Title">The window's title-bar text.</param>
/// <param name="Bounds">The window's frame in physical virtual-desktop pixels.</param>
/// <param name="ProcessId">The owning process id (0 when unknown).</param>
/// <param name="ProcessName">The owning process's image name without extension (empty when unknown).</param>
/// <param name="ClassName">The Win32 window class name (empty when unknown).</param>
/// <param name="OwnerHandle">The root owner window (<c>GA_ROOTOWNER</c>), or 0 when the window owns itself.</param>
/// <param name="IsForeground">True when this is the current foreground window.</param>
/// <param name="IsToolWindow">True for tool windows (palettes, tooltips, menus, dropdowns).</param>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    PhysicalRect Bounds,
    int ProcessId = 0,
    string ProcessName = "",
    string ClassName = "",
    nint OwnerHandle = 0,
    bool IsForeground = false,
    bool IsToolWindow = false)
{
    /// <summary>
    /// A minimal descriptor for a handle supplied directly (e.g. the CLI <c>--hwnd</c> / MCP
    /// <c>handle</c> argument) that was not found in an enumeration. Only the identity is known.
    /// </summary>
    /// <param name="handle">The native window handle (HWND).</param>
    /// <returns>A <see cref="WindowInfo"/> carrying just the handle.</returns>
    public static WindowInfo FromHandle(nint handle) => new(handle, string.Empty, default);
}
