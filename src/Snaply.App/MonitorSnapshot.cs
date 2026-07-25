using System.ComponentModel;
using System.Runtime.InteropServices;
using Snaply.Imaging;

namespace Snaply;

internal sealed partial record MonitorSnapshot(nint Handle, PixelRect Bounds, bool IsPrimary)
{
    private const uint MonitorInfoPrimary = 1;

    internal static IReadOnlyList<MonitorSnapshot> Enumerate()
    {
        var monitors = new List<MonitorSnapshot>();
        int monitorInfoError = 0;

        bool Callback(nint monitor, nint deviceContext, ref NativeRect bounds, nint data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                monitorInfoError = Marshal.GetLastPInvokeError();
                return false;
            }

            monitors.Add(new MonitorSnapshot(
                monitor,
                new PixelRect(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    checked(info.Monitor.Right - info.Monitor.Left),
                    checked(info.Monitor.Bottom - info.Monitor.Top)),
                (info.Flags & MonitorInfoPrimary) != 0));
            return true;
        }

        bool enumerated = EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero);
        if (monitorInfoError != 0)
        {
            throw new Win32Exception(monitorInfoError, "A display could not be inspected.");
        }

        if (!enumerated)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Displays could not be enumerated.");
        }

        if (monitors.Count == 0)
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
