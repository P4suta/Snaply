using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Snaply.Imaging;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Snaply;

internal sealed partial class ScreenCaptureService : IDisposable
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private readonly object _deviceLock = new();
    private readonly Dictionary<nint, MonitorCaptureItem> _monitorItems = [];
    private readonly RegionSelectionService _regionSelection = new();
    private CanvasDevice? _device = new();
    private Window? _appWindow;
    private bool _captureExclusionEnabled;
    private bool _disposed;

    internal void SetAppWindow(Window window, bool captureExclusionEnabled)
    {
        _appWindow = window;
        _captureExclusionEnabled = captureExclusionEnabled;
    }

    internal async Task<CapturedFrame?> CaptureAsync(CaptureMode mode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable.");
        }

        return mode switch
        {
            CaptureMode.Window => await CapturePickedItemAsync(cancellationToken),
            CaptureMode.Desktop => await CaptureDesktopAsync(cancellationToken),
            CaptureMode.Region => await CaptureRegionAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _regionSelection.Dispose();
        lock (_deviceLock)
        {
            _monitorItems.Clear();
            _device?.Dispose();
            _device = null;
        }
    }

    private async Task<CapturedFrame?> CapturePickedItemAsync(CancellationToken cancellationToken)
    {
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_appWindow));
        GraphicsCaptureItem? item = await picker.PickSingleItemAsync().AsTask(cancellationToken);
        if (item is null)
        {
            return null;
        }

        bool hidden = HideApp();
        try
        {
            await WaitForHiddenAppAsync(hidden, cancellationToken);
            CanvasBitmap bitmap = await CaptureItemAsSdrAsync(item, cancellationToken);
            return new CapturedFrame(bitmap);
        }
        finally
        {
            ShowAppIfHidden(hidden);
        }
    }

    private async Task<CapturedFrame> CaptureDesktopAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitorSnapshot> monitors = MonitorSnapshot.Enumerate();
        PixelRect desktop = PixelRect.Bounds(monitors.Select(static monitor => monitor.Bounds));
        ValidateSize(desktop.Width, desktop.Height);

        bool hidden = HideApp();
        try
        {
            await WaitForHiddenAppAsync(hidden, cancellationToken);
            CanvasDevice device = GetDevice();
            var target = new CanvasRenderTarget(
                device,
                desktop.Width,
                desktop.Height,
                96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);
            try
            {
                using CanvasDrawingSession drawing = target.CreateDrawingSession();
                drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

                foreach (MonitorSnapshot monitor in monitors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using CanvasBitmap bitmap = await CaptureMonitorAsSdrAsync(
                        monitor,
                        cancellationToken);
                    ValidateMonitorBitmap(bitmap, monitor);

                    drawing.DrawImage(
                        bitmap,
                        new Rect(
                            checked(monitor.Bounds.X - desktop.X),
                            checked(monitor.Bounds.Y - desktop.Y),
                            monitor.Bounds.Width,
                            monitor.Bounds.Height));
                }

                return new CapturedFrame(target);
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
        finally
        {
            ShowAppIfHidden(hidden);
        }
    }

    private async Task<CapturedFrame?> CaptureRegionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitorSnapshot> monitors = MonitorSnapshot.Enumerate();
        PixelRect? region = await _regionSelection.PickAsync(monitors, cancellationToken);
        if (region is null)
        {
            return null;
        }

        ValidateSize(region.Value.Width, region.Value.Height);
        bool hidden = HideApp();
        try
        {
            await WaitForHiddenAppAsync(hidden, cancellationToken);
            CanvasDevice device = GetDevice();
            var target = new CanvasRenderTarget(
                device,
                region.Value.Width,
                region.Value.Height,
                96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);
            try
            {
                using CanvasDrawingSession drawing = target.CreateDrawingSession();
                drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                bool drewMonitor = false;

                foreach (MonitorSnapshot monitor in monitors)
                {
                    PixelRect intersection = monitor.Bounds.Intersect(region.Value);
                    if (intersection.IsEmpty)
                    {
                        continue;
                    }

                    using CanvasBitmap bitmap = await CaptureMonitorAsSdrAsync(
                        monitor,
                        cancellationToken);
                    ValidateMonitorBitmap(bitmap, monitor);
                    PixelRect source = intersection.RelativeTo(monitor.Bounds);
                    PixelRect destination = intersection.RelativeTo(region.Value);
                    drawing.DrawImage(
                        bitmap,
                        ToRect(destination),
                        ToRect(source));
                    drewMonitor = true;
                }

                if (!drewMonitor)
                {
                    throw new InvalidOperationException("The selected region is no longer available.");
                }

                return new CapturedFrame(target);
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
        finally
        {
            ShowAppIfHidden(hidden);
        }
    }

    private async Task<CanvasBitmap> CaptureItemAsSdrAsync(
        GraphicsCaptureItem item,
        CancellationToken cancellationToken)
    {
        ValidateSize(item.Size.Width, item.Size.Height);
        CanvasDevice device = GetDevice();
        using Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.R16G16B16A16Float,
            1,
            item.Size);
        using GraphicsCaptureSession session = pool.CreateCaptureSession(item);
        var completion = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is not null && !completion.TrySetResult(frame))
            {
                frame.Dispose();
            }
        }

        void OnClosed(GraphicsCaptureItem sender, object args) =>
            completion.TrySetException(new InvalidOperationException("The capture source closed."));

        int deviceLost = 0;
        void OnDeviceLost(CanvasDevice sender, object args)
        {
            Interlocked.Exchange(ref deviceLost, 1);
            InvalidateDevice(sender);
            completion.TrySetException(new InvalidOperationException("The graphics device was lost."));
        }

        pool.FrameArrived += OnFrameArrived;
        item.Closed += OnClosed;
        device.DeviceLost += OnDeviceLost;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FirstFrameTimeout);
        using CancellationTokenRegistration registration = timeout.Token.Register(
            () => completion.TrySetCanceled(timeout.Token));

        try
        {
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();
            using Direct3D11CaptureFrame frame = await completion.Task;
            ValidateSize(frame.ContentSize.Width, frame.ContentSize.Height);
            using CanvasBitmap source = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
            return ConvertToSdr(device, source, frame.ContentSize, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The capture source did not produce a frame in time.");
        }
        finally
        {
            pool.FrameArrived -= OnFrameArrived;
            item.Closed -= OnClosed;
            device.DeviceLost -= OnDeviceLost;
            if (Volatile.Read(ref deviceLost) != 0)
            {
                device.Dispose();
            }
        }
    }

    private static CanvasBitmap ConvertToSdr(
        CanvasDevice device,
        CanvasBitmap source,
        Windows.Graphics.SizeInt32 contentSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] scRgb = source.GetPixelBytes(
            0,
            0,
            contentSize.Width,
            contentSize.Height);
        int pixelCount = checked(contentSize.Width * contentSize.Height);
        int expectedLength = checked(pixelCount * 8);
        if (scRgb.Length != expectedLength)
        {
            throw new InvalidDataException("The FP16 capture buffer has an invalid length.");
        }

        var bgra = new byte[checked(pixelCount * 4)];
        // DXGI R16G16B16A16_FLOAT is little-endian RGBA half-float on Windows.
        ReadOnlySpan<Half> rgba = MemoryMarshal.Cast<byte, Half>(scRgb);
        _ = ScRgbToneMapper.ConvertToBgra8(rgba, bgra, cancellationToken);
        return CanvasBitmap.CreateFromBytes(
            device,
            bgra,
            contentSize.Width,
            contentSize.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96,
            CanvasAlphaMode.Premultiplied);
    }

    private CanvasDevice GetDevice()
    {
        lock (_deviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _device ??= new CanvasDevice();
        }
    }

    private async Task<CanvasBitmap> CaptureMonitorAsSdrAsync(
        MonitorSnapshot monitor,
        CancellationToken cancellationToken)
    {
        GraphicsCaptureItem item = GetMonitorItem(monitor);
        try
        {
            return await CaptureItemAsSdrAsync(item, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RemoveMonitorItem(monitor.Handle, item);
            throw;
        }
    }

    private void InvalidateDevice(CanvasDevice device)
    {
        lock (_deviceLock)
        {
            if (ReferenceEquals(_device, device))
            {
                _device = null;
            }
        }
    }

    private GraphicsCaptureItem GetMonitorItem(MonitorSnapshot monitor)
    {
        lock (_deviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitorItems.TryGetValue(monitor.Handle, out MonitorCaptureItem cached)
                && cached.Bounds == monitor.Bounds)
            {
                return cached.Item;
            }

            GraphicsCaptureItem item = CreateItemForMonitor(monitor.Handle);
            _monitorItems[monitor.Handle] = new MonitorCaptureItem(monitor.Bounds, item);
            return item;
        }
    }

    private void RemoveMonitorItem(nint handle, GraphicsCaptureItem item)
    {
        lock (_deviceLock)
        {
            if (_monitorItems.TryGetValue(handle, out MonitorCaptureItem cached)
                && ReferenceEquals(cached.Item, item))
            {
                _monitorItems.Remove(handle);
            }
        }
    }

    private bool HideApp()
    {
        if (_appWindow is null)
        {
            return false;
        }

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(_appWindow);
        if (_captureExclusionEnabled
            && GetWindowDisplayAffinity(handle, out uint affinity)
            && affinity == WdaExcludeFromCapture)
        {
            return false;
        }

        _appWindow.AppWindow.Hide();
        _ = DwmFlush();
        return true;
    }

    private void ShowAppIfHidden(bool hidden)
    {
        if (hidden && _appWindow is not null)
        {
            _appWindow.AppWindow.Show();
            _appWindow.Activate();
        }
    }

    private static async Task WaitForHiddenAppAsync(
        bool hidden,
        CancellationToken cancellationToken)
    {
        if (hidden)
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    private void ValidateSize(int width, int height)
    {
        int maximum = checked((int)GetDevice().MaximumBitmapSizeInPixels);
        if (width <= 0 || height <= 0 || width > maximum || height > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Capture dimensions are unsupported.");
        }

        _ = checked((long)width * height * 8);
    }

    private static void ValidateMonitorBitmap(CanvasBitmap bitmap, MonitorSnapshot monitor)
    {
        if (bitmap.SizeInPixels.Width != monitor.Bounds.Width
            || bitmap.SizeInPixels.Height != monitor.Bounds.Height)
        {
            throw new InvalidOperationException("Display topology changed during capture.");
        }
    }

    private static Rect ToRect(PixelRect rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        nint pointer = interop.CreateForMonitor(monitor, GraphicsCaptureItemId);
        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private readonly record struct MonitorCaptureItem(PixelRect Bounds, GraphicsCaptureItem Item);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        public nint CreateForWindow(nint window, in Guid iid);

        public nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
