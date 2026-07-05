using System.Runtime.InteropServices;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Platform;

/// <summary>
/// Enumerates display monitors over the Win32 monitor list (<c>EnumDisplayMonitors</c>),
/// ordered primary-first so the returned <see cref="MonitorInfo.Index"/> matches the index
/// <see cref="WgcScreenCaptureService.CaptureMonitorAsync"/> accepts.
/// </summary>
public sealed class MonitorEnumerationService : IMonitorEnumerationService
{
    private const int MonitorInfofPrimary = 0x1;

    /// <inheritdoc/>
    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var raw = new List<(IntPtr Handle, RECT Bounds, bool Primary)>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                bool primary = (info.dwFlags & MonitorInfofPrimary) != 0;
                raw.Add((hMonitor, info.rcMonitor, primary));
            }

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // Primary first so index 0 == primary, matching WgcScreenCaptureService.
        raw.Sort((a, b) => b.Primary.CompareTo(a.Primary));

        var monitors = new List<MonitorInfo>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            (IntPtr handle, RECT bounds, bool primary) = raw[i];
            var rect = new PhysicalRect(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top);
            monitors.Add(new MonitorInfo(i, rect, DpiProvider.GetForMonitor(handle), primary));
        }

        return monitors;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
