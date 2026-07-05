using System.Runtime.InteropServices;
using Snaply.Core.Geometry;

namespace Snaply.Platform;

/// <summary>
/// Bridges Win32 per-monitor DPI queries to the platform-free
/// <see cref="Dpi"/> value the capture pipeline works in. 96 DPI == 100% scale.
/// </summary>
public static class DpiProvider
{
    private const int MdtEffectiveDpi = 0;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Effective DPI for the window's current monitor.</summary>
    /// <param name="hwnd">Handle of the window to query.</param>
    /// <returns>The window's effective DPI, or <see cref="Dpi.Default"/> on failure.</returns>
    public static Dpi GetForWindow(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? Dpi.Default : new Dpi(dpi);
    }

    /// <summary>Effective DPI (MDT_EFFECTIVE_DPI) for a monitor handle.</summary>
    /// <param name="hmonitor">Handle of the monitor to query.</param>
    /// <returns>The monitor's effective DPI, or <see cref="Dpi.Default"/> on failure.</returns>
    public static Dpi GetForMonitor(IntPtr hmonitor)
    {
        // GetDpiForMonitor returns S_OK (0) on success and writes an identical X/Y
        // effective DPI; fall back to the 96-DPI baseline on any failure.
        if (GetDpiForMonitor(hmonitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX != 0)
        {
            return new Dpi(dpiX);
        }

        return Dpi.Default;
    }
}
