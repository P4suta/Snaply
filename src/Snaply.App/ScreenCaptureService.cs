using System.Collections.Concurrent;
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
    private const int CaptureSourceBytesPerPixel = 24;
    private const int CompositeBytesPerPixel = 4;
    private const int RenderInputBytesPerPixel = 8;
    private const int RenderOutputBytesPerPixel = 16;
    private const long MaximumCaptureBudgetBytes = 1_610_612_736;
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private readonly Window _appWindow;
    private readonly bool _captureExclusionEnabled;
    private readonly object _deviceLock = new();
    private readonly Dictionary<nint, MonitorCaptureItem> _monitorItems = [];
    private readonly RegionSelectionService _regionSelection;
    private CanvasDevice? _device;
    private bool _disposed;

    internal ScreenCaptureService(Window appWindow, bool captureExclusionEnabled)
    {
        _appWindow = appWindow;
        _captureExclusionEnabled = captureExclusionEnabled;
        _regionSelection = new RegionSelectionService(ActivateOwner);
    }

    internal Task<CapturedFrame?> CaptureAsync(CaptureMode mode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable.");
        }

        return mode switch
        {
            CaptureMode.Window => CapturePickedItemAsync(cancellationToken),
            CaptureMode.Desktop => CaptureDesktopAsync(cancellationToken),
            CaptureMode.Region => CaptureRegionAsync(cancellationToken),
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

    internal bool IsGraphicsDeviceLost(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is GraphicsDeviceLostException)
            {
                return true;
            }

            lock (_deviceLock)
            {
                if (_device?.IsDeviceLost(current.HResult) is true)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal void ResetGraphicsDevice()
    {
        CanvasDevice? device;
        lock (_deviceLock)
        {
            device = _device;
            _device = null;
        }

        device?.Dispose();
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

        ValidateSize(item.Size.Width, item.Size.Height);
        ValidateCaptureBudget(
            checked((long)item.Size.Width * item.Size.Height),
            new PixelRect(0, 0, item.Size.Width, item.Size.Height));
        bool hidden = HideApp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CanvasBitmap bitmap = await CaptureItemAsSdrAsync(item, cancellationToken);
            return new CapturedFrame(bitmap);
        }
        finally
        {
            ShowAppIfHidden(hidden);
        }
    }

    private async Task<CapturedFrame?> CaptureDesktopAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitorSnapshot> monitors = MonitorSnapshot.Enumerate();
        PixelRect desktop = PixelRect.Bounds(monitors.Select(static monitor => monitor.Bounds));
        ValidateSize(desktop.Width, desktop.Height);
        ValidateCaptureBudget(monitors, desktop);

        bool hidden = HideApp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CapturedMonitorBitmap> captures =
                await CaptureMonitorBitmapsAsync(monitors, cancellationToken);
            try
            {
                CapturedFrame frame = await Task.Run(
                    () => ComposeCaptures(captures, desktop, cancellationToken),
                    cancellationToken);
                return frame;
            }
            finally
            {
                DisposeCaptures(captures);
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
        MonitorSnapshot[] intersecting = monitors
            .Where(monitor => !monitor.Bounds.Intersect(region.Value).IsEmpty)
            .ToArray();
        if (intersecting.Length == 0)
        {
            throw new InvalidOperationException("The selected region is no longer available.");
        }

        ValidateCaptureBudget(intersecting, region.Value);
        bool hidden = HideApp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CapturedMonitorBitmap> captures =
                await CaptureMonitorBitmapsAsync(intersecting, cancellationToken);
            try
            {
                CapturedFrame frame = await Task.Run(
                    () => ComposeCaptures(captures, region.Value, cancellationToken),
                    cancellationToken);
                return frame;
            }
            finally
            {
                DisposeCaptures(captures);
            }
        }
        finally
        {
            ShowAppIfHidden(hidden);
        }
    }

    private CapturedFrame ComposeCaptures(
        IReadOnlyList<CapturedMonitorBitmap> captures,
        PixelRect output,
        CancellationToken cancellationToken)
    {
        CanvasRenderTarget? target = new(
            GetDevice(),
            output.Width,
            output.Height,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        try
        {
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            foreach (CapturedMonitorBitmap capture in captures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateMonitorBitmap(capture.Bitmap, capture.Monitor);
                PixelRect intersection = capture.Monitor.Bounds.Intersect(output);
                if (intersection.IsEmpty)
                {
                    continue;
                }

                drawing.DrawImage(
                    capture.Bitmap,
                    ToRect(intersection.RelativeTo(output)),
                    ToRect(intersection.RelativeTo(capture.Monitor.Bounds)));
            }

            var result = new CapturedFrame(target);
            target = null;
            return result;
        }
        finally
        {
            target?.Dispose();
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
            completion.TrySetException(new GraphicsDeviceLostException());
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
            ValidateCaptureBudget(
                checked((long)frame.ContentSize.Width * frame.ContentSize.Height),
                new PixelRect(
                    0,
                    0,
                    frame.ContentSize.Width,
                    frame.ContentSize.Height));
            using CanvasBitmap source = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
            return await Task.Run(
                () => ConvertToSdr(device, source, frame.ContentSize, cancellationToken),
                cancellationToken);
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

    private async Task<IReadOnlyList<CapturedMonitorBitmap>> CaptureMonitorBitmapsAsync(
        IReadOnlyList<MonitorSnapshot> monitors,
        CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(Math.Min(monitors.Count, 4));
        var completedCaptures = new ConcurrentBag<CapturedMonitorBitmap>();
        Task<CapturedMonitorBitmap>[] tasks = monitors
            .Select(async monitor =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    CanvasBitmap bitmap = await CaptureMonitorAsSdrAsync(
                        monitor,
                        cancellationToken);
                    var capture = new CapturedMonitorBitmap(monitor, bitmap);
                    completedCaptures.Add(capture);
                    return capture;
                }
                finally
                {
                    concurrency.Release();
                }
            })
            .ToArray();

        try
        {
            return await Task.WhenAll(tasks);
        }
        catch
        {
            foreach (CapturedMonitorBitmap capture in completedCaptures)
            {
                capture.Bitmap.Dispose();
            }

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
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(_appWindow);
        if (_captureExclusionEnabled
            && GetWindowDisplayAffinity(handle, out uint affinity)
            && affinity == WdaExcludeFromCapture)
        {
            return false;
        }

        _appWindow.AppWindow.Hide();
        int result = DwmFlush();
        if (result < 0)
        {
            _appWindow.AppWindow.Show();
            _appWindow.Activate();
            Marshal.ThrowExceptionForHR(result);
        }

        return true;
    }

    private void ShowAppIfHidden(bool hidden)
    {
        if (hidden && !_disposed)
        {
            _appWindow.AppWindow.Show();
            _appWindow.Activate();
        }
    }

    private void ValidateSize(int width, int height)
    {
        int maximum = checked((int)GetDevice().MaximumBitmapSizeInPixels);
        if (width <= 0 || height <= 0 || width > maximum || height > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Capture dimensions are unsupported.");
        }

        PixelSize rendered = BeautifyLayout.Compute(new PixelSize(width, height)).Canvas;
        if (rendered.Width > maximum || rendered.Height > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Capture dimensions leave no room for the rendered output.");
        }

        _ = checked((long)width * height);
    }

    private static void ValidateCaptureBudget(
        IReadOnlyList<MonitorSnapshot> monitors,
        PixelRect output)
    {
        long sourcePixels = monitors.Sum(
            static monitor => checked((long)monitor.Bounds.Width * monitor.Bounds.Height));
        ValidateCaptureBudget(sourcePixels, output);
    }

    private static void ValidateCaptureBudget(long sourcePixels, PixelRect output)
    {
        long outputPixels = output.Size.Area;
        long renderedPixels = BeautifyLayout.Compute(output.Size).Canvas.Area;
        long capturePeak = checked(
            (sourcePixels * CaptureSourceBytesPerPixel)
            + (outputPixels * CompositeBytesPerPixel));
        long renderAndDeliveryPeak = checked(
            (outputPixels * RenderInputBytesPerPixel)
            + (renderedPixels * RenderOutputBytesPerPixel));
        long estimatedBytes = Math.Max(capturePeak, renderAndDeliveryPeak);
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long budget;
        if (memory.HighMemoryLoadThresholdBytes > 0 && memory.MemoryLoadBytes > 0)
        {
            long headroom = Math.Max(
                0,
                memory.HighMemoryLoadThresholdBytes - memory.MemoryLoadBytes);
            budget = Math.Min(MaximumCaptureBudgetBytes, headroom / 2);
        }
        else if (memory.TotalAvailableMemoryBytes > memory.TotalCommittedBytes)
        {
            long headroom = memory.TotalAvailableMemoryBytes - memory.TotalCommittedBytes;
            budget = Math.Min(MaximumCaptureBudgetBytes, headroom / 2);
        }
        else
        {
            budget = MaximumCaptureBudgetBytes;
        }

        if (estimatedBytes > budget)
        {
            throw new InvalidOperationException("The requested capture exceeds the safe memory budget.");
        }
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

    private static void DisposeCaptures(IReadOnlyList<CapturedMonitorBitmap> captures)
    {
        foreach (CapturedMonitorBitmap capture in captures)
        {
            capture.Bitmap.Dispose();
        }
    }

    private void ActivateOwner()
    {
        if (!_disposed)
        {
            _appWindow.Activate();
        }
    }

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

    private readonly record struct CapturedMonitorBitmap(
        MonitorSnapshot Monitor,
        CanvasBitmap Bitmap);

    private sealed class GraphicsDeviceLostException : InvalidOperationException
    {
        private const int DeviceRemoved = unchecked((int)0x887A0005);

        internal GraphicsDeviceLostException()
            : base("The graphics device was lost.")
        {
            HResult = DeviceRemoved;
        }

        public GraphicsDeviceLostException(string message) : base(message)
        {
        }

        public GraphicsDeviceLostException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

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
