using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Platform;

/// <summary>
/// Enumerates windows over the Win32 window list. <see cref="EnumerateTopLevelWindows"/> hands
/// back the user-pickable windows in front-to-back z-order (so the picker overlay resolves the
/// frontmost window under the cursor by taking the first match). <see cref="GetForegroundWindow"/>
/// and <see cref="EnumerateRelatedWindows"/> add the targeting AI clients need: the active window,
/// and a window together with its owned dialogs/popups. Bounds come from the DWM extended frame so
/// they line up with the visible window (not the invisible resize border).
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

    // GetAncestor flag: the owner at the root of the owner chain (a popup's owning app window).
    private const uint GaRootOwner = 3;

    // Minimum edge (physical px) below which a window is treated as degenerate/hidden.
    private const int MinimumWindowExtent = 8;

    /// <inheritdoc/>
    public IReadOnlyList<WindowInfo> EnumerateTopLevelWindows()
    {
        uint ownProcessId = GetCurrentProcessId();
        IntPtr foreground = GetForegroundWindowHandle();
        var windows = new List<WindowInfo>();

        // EnumWindows visits top-level windows in z-order, topmost first; appending in
        // callback order preserves that so the picker's first hit is the frontmost.
        bool Callback(IntPtr hwnd, IntPtr lparam)
        {
            if (TryDescribeWindow(hwnd, ownProcessId, foreground, relaxed: false, out WindowInfo? info))
            {
                windows.Add(info);
            }

            return true;
        }

        EnumWindows(Callback, IntPtr.Zero);
        return windows;
    }

    /// <inheritdoc/>
    public WindowInfo? GetForegroundWindow()
    {
        IntPtr foreground = GetForegroundWindowHandle();
        if (foreground == IntPtr.Zero)
        {
            return null;
        }

        // Relaxed and without an own-process exclusion: whatever is genuinely in front is a valid
        // "active window" target, including tool/untitled windows.
        return TryDescribeWindow(foreground, excludeProcessId: null, foreground, relaxed: true, out WindowInfo? info)
            ? info
            : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WindowInfo> EnumerateRelatedWindows(nint target)
    {
        IntPtr rootOwner = RootOwner(target);
        IntPtr foreground = GetForegroundWindowHandle();
        var group = new List<WindowInfo>();

        bool Callback(IntPtr hwnd, IntPtr lparam)
        {
            // Include the owner itself and every top-level window whose owner chain roots at it —
            // owned file pickers, modal dialogs, popups, menus and dropdowns. Relaxed on purpose so
            // tool/untitled popups still contribute their bounds to the composite region.
            if (RootOwner(hwnd) == rootOwner
                && TryDescribeWindow(hwnd, excludeProcessId: null, foreground, relaxed: true, out WindowInfo? info))
            {
                group.Add(info);
            }

            return true;
        }

        EnumWindows(Callback, IntPtr.Zero);
        return group;
    }

    private static bool TryDescribeWindow(IntPtr hwnd, uint? excludeProcessId, IntPtr foreground, bool relaxed, [NotNullWhen(true)] out WindowInfo? info)
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

        _ = GetWindowThreadProcessId(hwnd, out uint processId);

        // Never offer our own windows to the picker (the app window, the overlay itself).
        if (excludeProcessId is { } exclude && processId == exclude)
        {
            return false;
        }

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        bool isToolWindow = (exStyle & WsExToolWindow) != 0;

        string title = GetWindowTitle(hwnd);

        // The picker only offers primary windows: skip tool windows and untitled chrome. Group /
        // foreground callers pass relaxed:true so those still count.
        if (!relaxed && (isToolWindow || string.IsNullOrWhiteSpace(title)))
        {
            return false;
        }

        PhysicalRect bounds = GetWindowBounds(hwnd);
        if (bounds.Width < MinimumWindowExtent || bounds.Height < MinimumWindowExtent)
        {
            return false;
        }

        info = new WindowInfo(
            hwnd,
            title,
            bounds,
            ProcessId: (int)processId,
            ProcessName: GetProcessName(processId),
            ClassName: GetClassName(hwnd),
            OwnerHandle: NormalizedOwner(hwnd),
            IsForeground: hwnd == foreground,
            IsToolWindow: isToolWindow);
        return true;
    }

    // The root owner, or 0 when the window owns itself (a normal top-level window).
    private static IntPtr NormalizedOwner(IntPtr hwnd)
    {
        IntPtr owner = GetAncestor(hwnd, GaRootOwner);
        return owner == hwnd ? IntPtr.Zero : owner;
    }

    // The root of the owner chain, treating an unowned window as its own root so group membership
    // is a simple equality check.
    private static IntPtr RootOwner(IntPtr hwnd)
    {
        IntPtr owner = GetAncestor(hwnd, GaRootOwner);
        return owner == IntPtr.Zero ? hwnd : owner;
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process exited between enumeration and lookup, or has no accessible name.
            return string.Empty;
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int copied = GetClassName(hwnd, ref buffer[0], buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer[..copied]);
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

    private static IntPtr GetForegroundWindowHandle() => NativeGetForegroundWindow();

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hwnd, ref char lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativeGetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
}
