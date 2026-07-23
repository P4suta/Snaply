using System.Runtime.InteropServices;
using Snaply.Imaging;

namespace Snaply;

internal sealed partial record MonitorSnapshot(nint Handle, PixelRect Bounds, bool IsPrimary)
{
    private const uint MonitorInfoPrimary = 1;

    internal static IReadOnlyList<MonitorSnapshot> Enumerate()
    {
        var monitors = new List<MonitorSnapshot>();

        bool Callback(nint monitor, nint deviceContext, ref NativeRect bounds, nint data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new MonitorSnapshot(
                    monitor,
                    new PixelRect(
                        info.Monitor.Left,
                        info.Monitor.Top,
                        checked(info.Monitor.Right - info.Monitor.Left),
                        checked(info.Monitor.Bottom - info.Monitor.Top)),
                    (info.Flags & MonitorInfoPrimary) != 0));
            }

            return true;
        }

        if (!EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero) || monitors.Count == 0)
        {
            throw new InvalidOperationException("No display is available.");
        }

        monitors.Sort(static (left, right) => right.IsPrimary.CompareTo(left.IsPrimary));
        return monitors;
    }

    private delegate bool MonitorCallback(
        nint monitor,
        nint deviceContext,
        ref NativeRect bounds,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(
        nint deviceContext,
        nint clip,
        MonitorCallback callback,
        nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
