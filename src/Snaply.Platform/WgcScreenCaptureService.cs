using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Snaply.Platform;

/// <summary>
/// Captures a single still frame from a monitor via Windows.Graphics.Capture, using
/// a Win2D <see cref="CanvasDevice"/> as the D3D device and converting the captured
/// D3D surface to plain BGRA bytes. Free-threaded frame pool: capture does NOT need
/// the app's DispatcherQueue and can run off the UI thread.
/// </summary>
public sealed class WgcScreenCaptureService : IScreenCaptureService, IDisposable
{
    private const int MonitorInfofPrimary = 0x1;

    // IID of Windows.Graphics.Capture.GraphicsCaptureItem, passed to the interop factory.
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly CanvasDevice _device = new();
    private readonly ILogger<WgcScreenCaptureService> _logger;
    private bool _disposed;

    /// <summary>Creates the capture service.</summary>
    /// <param name="logger">Structured logger for capture failures.</param>
    public WgcScreenCaptureService(ILogger<WgcScreenCaptureService> logger) => _logger = logger;

    /// <summary>Native interop factory that vends a capture item for an HWND/HMONITOR.</summary>
    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, in Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, in Guid iid);
    }

    /// <inheritdoc/>
    public async Task<Result<CapturedImage>> CaptureMonitorAsync(int monitorIndex, CancellationToken cancellationToken = default)
    {
        List<MonitorInfo> monitors = EnumerateMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
        {
            return Result<CapturedImage>.Fail(ErrorCodes.CaptureMonitor, $"Monitor index {monitorIndex} is out of range (found {monitors.Count}).");
        }

        MonitorInfo monitor = monitors[monitorIndex];
        try
        {
            using CanvasBitmap bitmap = await CaptureItemAsync(CreateItemForMonitor(monitor.Handle), cancellationToken).ConfigureAwait(false);
            var size = new PhysicalSize((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            return Result<CapturedImage>.Ok(new CapturedImage(size, bitmap.GetPixelBytes(), DpiProvider.GetForMonitor(monitor.Handle)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PlatformLog.CaptureFailed(_logger, ErrorCodes.CaptureMonitor, ex.Message, ex);
            return Result<CapturedImage>.Fail(ErrorCodes.CaptureMonitor, ex.Message, ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<CapturedImage>> CaptureWindowAsync(nint windowHandle, CancellationToken cancellationToken = default)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return Result<CapturedImage>.Fail(ErrorCodes.CaptureWindow, "Window handle must be non-zero.");
        }

        try
        {
            using CanvasBitmap bitmap = await CaptureItemAsync(CreateItemForWindow(windowHandle), cancellationToken).ConfigureAwait(false);
            var size = new PhysicalSize((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            return Result<CapturedImage>.Ok(new CapturedImage(size, bitmap.GetPixelBytes(), DpiProvider.GetForWindow(windowHandle)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PlatformLog.CaptureFailed(_logger, ErrorCodes.CaptureWindow, ex.Message, ex);
            return Result<CapturedImage>.Fail(ErrorCodes.CaptureWindow, ex.Message, ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<CapturedImage>> CaptureRegionAsync(PhysicalRect region, CancellationToken cancellationToken = default)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return Result<CapturedImage>.Fail(ErrorCodes.CaptureRegion, "Region must have a positive width and height.");
        }

        MonitorInfo? monitor = FindMonitorForRegion(region);
        if (monitor is null)
        {
            return Result<CapturedImage>.Fail(ErrorCodes.CaptureRegion, "No monitor contains the requested region.");
        }

        try
        {
            using CanvasBitmap full = await CaptureItemAsync(CreateItemForMonitor(monitor.Handle), cancellationToken).ConfigureAwait(false);

            // Virtual-desktop physical coords -> monitor-local pixels before cropping.
            double localX = region.X - monitor.Left;
            double localY = region.Y - monitor.Top;

            using var target = new CanvasRenderTarget(_device, region.Width, region.Height, 96);
            using (CanvasDrawingSession ds = target.CreateDrawingSession())
            {
                ds.DrawImage(
                    full,
                    new Rect(0, 0, region.Width, region.Height),
                    new Rect(localX, localY, region.Width, region.Height));
            }

            return Result<CapturedImage>.Ok(new CapturedImage(region.Size, target.GetPixelBytes(), DpiProvider.GetForMonitor(monitor.Handle)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PlatformLog.CaptureFailed(_logger, ErrorCodes.CaptureRegion, ex.Message, ex);
            return Result<CapturedImage>.Fail(ErrorCodes.CaptureRegion, ex.Message, ex);
        }
    }

    /// <summary>Start capture on a free-threaded pool and await exactly one frame, then tear down.</summary>
    private async Task<CanvasBitmap> CaptureItemAsync(GraphicsCaptureItem item, CancellationToken cancellationToken)
    {
        Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);
        GraphicsCaptureSession session = framePool.CreateCaptureSession(item);

        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
        {
            Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame is not null)
            {
                tcs.TrySetResult(frame);
            }
        }

        framePool.FrameArrived += OnFrameArrived;

        // Newer capture sessions can hide the cursor and drop the yellow capture
        // border; both are optional and unpackaged apps may lack borderless consent.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
        {
            session.IsCursorCaptureEnabled = false;
        }

        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try
            {
                // Dropping the yellow "capture in progress" border needs runtime
                // borderless consent. For unpackaged apps (no package identity) this
                // request commonly no-ops/denies — only disable the border if it was
                // actually granted, otherwise keep the border rather than failing.
                if (await TryRequestBorderlessAccessAsync().ConfigureAwait(false))
                {
                    session.IsBorderRequired = false;
                }
            }
            catch (Exception ex)
            {
                // No borderless consent (unpackaged) — continue with the border rather than failing.
                PlatformLog.BorderlessConsentUnavailable(_logger, ex);
            }
        }

        await using CancellationTokenRegistration registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            session.StartCapture();
            using Direct3D11CaptureFrame frame = await tcs.Task.ConfigureAwait(false);
            return CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
            session.Dispose();
            framePool.Dispose();
        }
    }

    /// <summary>
    /// Ask the OS for borderless capture consent. Guarded by both the compiler version
    /// check and <see cref="ApiInformation"/> because <c>GraphicsCaptureAccess</c> only
    /// exists on newer builds. Returns <c>true</c> only when access is explicitly
    /// granted; any denial/no-op/throw yields <c>false</c> so the caller keeps the
    /// border and the capture still succeeds.
    /// </summary>
    private static async Task<bool> TryRequestBorderlessAccessAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
        {
            return false;
        }

        try
        {
            AppCapabilityAccessStatus status =
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception)
        {
            // Unpackaged apps with no package identity typically land here.
            return false;
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        IntPtr itemPointer = interop.CreateForMonitor(hmonitor, GraphicsCaptureItemGuid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        IntPtr itemPointer = interop.CreateForWindow(hwnd, GraphicsCaptureItemGuid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static MonitorInfo? FindMonitorForRegion(PhysicalRect region)
    {
        List<MonitorInfo> monitors = EnumerateMonitors();
        int centerX = region.X + (region.Width / 2);
        int centerY = region.Y + (region.Height / 2);

        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (MonitorInfo m in monitors)
        {
            if (centerX >= m.Left && centerX < m.Right && centerY >= m.Top && centerY < m.Bottom)
            {
                return m;
            }

            // Fall back to the monitor with the largest overlap with the region.
            int ix = Math.Max(0, Math.Min(region.X + region.Width, m.Right) - Math.Max(region.X, m.Left));
            int iy = Math.Max(0, Math.Min(region.Y + region.Height, m.Bottom) - Math.Max(region.Y, m.Top));
            long area = (long)ix * iy;
            if (area > bestArea)
            {
                bestArea = area;
                best = m;
            }
        }

        return best;
    }

    private static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                bool primary = (info.dwFlags & MonitorInfofPrimary) != 0;
                monitors.Add(new MonitorInfo(hMonitor, info.rcMonitor, primary));
            }

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // Primary monitor first so index 0 == primary.
        monitors.Sort((a, b) => b.Primary.CompareTo(a.Primary));
        return monitors;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.Dispose();
    }

    private sealed record MonitorInfo(IntPtr Handle, RECT Bounds, bool Primary)
    {
        public int Left => Bounds.left;

        public int Top => Bounds.top;

        public int Right => Bounds.right;

        public int Bottom => Bounds.bottom;
    }

    // --- Win32 monitor enumeration ---
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
