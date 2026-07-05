using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Platform;

/// <summary>
/// Enumerates the user-pickable top-level windows over the Win32 window list.
/// <see cref="EnumWindows"/> hands windows back in front-to-back z-order, which is
/// preserved so the picker overlay can resolve the frontmost window under the
/// cursor by taking the first match. Bounds come from the DWM extended frame so
/// the highlight lines up with the visible window (not the invisible resize border).
/// </summary>
public sealed class WindowEnumerationService : IWindowEnumerationService
{
    // GetWindowLongPtr index for the extended window styles.
    private const int GwlExStyle = -20;

    // Extended styles: tool windows are palettes/tooltips that should never be picked.
    private const long WsExToolWindow = 0x0000_0080;

    // DwmGetWindowAttribute attributes.
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    // Minimum edge (physical px) below which a window is treated as degenerate/hidden.
    private const int MinimumWindowExtent = 8;

    /// <inheritdoc/>
    public IReadOnlyList<WindowInfo> EnumerateTopLevelWindows()
    {
        uint ownProcessId = GetCurrentProcessId();
        var windows = new List<WindowInfo>();

        // EnumWindows visits top-level windows in z-order, topmost first; appending in
        // callback order preserves that so the picker's first hit is the frontmost.
        bool Callback(IntPtr hwnd, IntPtr lparam)
        {
            if (TryDescribeWindow(hwnd, ownProcessId, out WindowInfo? info))
            {
                windows.Add(info);
            }

            return true;
        }

        EnumWindows(Callback, IntPtr.Zero);
        return windows;
    }

    private static bool TryDescribeWindow(IntPtr hwnd, uint ownProcessId, [NotNullWhen(true)] out WindowInfo? info)
    {
        info = null;

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        // Cloaked windows are present but not shown (ghost UWP/store windows, virtual
        // desktops other than the current one). DWMWA_CLOAKED != 0 means "hidden".
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        // Never offer our own windows (the app window, the overlay itself).
        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == ownProcessId)
        {
            return false;
        }

        // Skip tool windows: palettes, tooltips and other non-primary chrome.
        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExToolWindow) != 0)
        {
            return false;
        }

        string title = GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        PhysicalRect bounds = GetWindowBounds(hwnd);
        if (bounds.Width < MinimumWindowExtent || bounds.Height < MinimumWindowExtent)
        {
            return false;
        }

        info = new WindowInfo(hwnd, title, bounds);
        return true;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        // +1 for the NUL terminator GetWindowText writes; trim it back off afterwards.
        char[] buffer = new char[length + 1];
        int copied = GetWindowText(hwnd, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }

    private static PhysicalRect GetWindowBounds(IntPtr hwnd)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS is the visible frame in physical, virtual-desktop
        // pixels — the same space capture and the overlay work in. Fall back to the
        // (slightly larger) window rect if the DWM query fails.
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out RECT frame, Marshal.SizeOf<RECT>()) == 0)
        {
            return ToRect(frame);
        }

        return GetWindowRect(hwnd, out RECT windowRect) ? ToRect(windowRect) : default;
    }

    private static PhysicalRect ToRect(RECT r) =>
        new(r.left, r.top, r.right - r.left, r.bottom - r.top);

    // --- Win32 window enumeration ---
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
}
