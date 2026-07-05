using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Snaply.Platform;
using Snaply.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace Snaply;

/// <summary>
/// A borderless, always-on-top overlay covering the entire <em>virtual desktop</em>
/// (every monitor). It freezes a screenshot of each monitor as its backdrop, lets the
/// user drag out a selection rectangle spanning any display, and returns the picked
/// region in virtual-desktop physical pixels (or <c>null</c> on cancel). The returned
/// origin may be negative when a secondary monitor sits left of / above the primary,
/// which is exactly what <see cref="IScreenCaptureService.CaptureRegionAsync"/> expects.
/// </summary>
public sealed partial class RegionOverlayWindow : Window
{
    // Virtual-screen metrics (bounding box of all monitors, in physical pixels for a
    // per-monitor-DPI-aware process).
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int MonitorInfofPrimary = 0x1;

    private readonly TaskCompletionSource<PhysicalRect?> _result = new();

    private double _scale = 1.0;
    private int _virtualLeft;
    private int _virtualTop;
    private Point _start;
    private bool _dragging;
    private bool _closed;

    /// <summary>Creates the overlay window and hooks its close handler.</summary>
    public RegionOverlayWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    /// <summary>Show the overlay and await the region the user drags out.</summary>
    /// <param name="capture">Capture service used to freeze each monitor as the backdrop.</param>
    /// <returns>The picked region in virtual-desktop physical pixels, or <c>null</c> on cancel.</returns>
    public async Task<PhysicalRect?> PickRegionAsync(IScreenCaptureService capture)
    {
        ConfigureWindow();

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _scale = DpiProvider.GetForWindow(hwnd).Scale;

        // Freeze each monitor as the backdrop so the user sees what they pick.
        await ComposeBackdropAsync(capture);

        Activate();
        RootGrid.Focus(FocusState.Programmatic);

        return await _result.Task;
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter existing)
        {
            existing.SetBorderAndTitleBar(false, false);
            existing.IsAlwaysOnTop = true;
            existing.IsResizable = false;
            existing.IsMaximizable = false;
            existing.IsMinimizable = false;
        }
        else
        {
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            AppWindow.SetPresenter(presenter);
        }

        // Cover the whole virtual desktop. AppWindow coordinates are physical pixels
        // and may be negative (secondary monitor left of / above the primary).
        _virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        _virtualTop = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        AppWindow.MoveAndResize(new RectInt32(_virtualLeft, _virtualTop, width, height));
    }

    /// <summary>
    /// Capture every monitor and lay each frozen image onto the backdrop at its
    /// position inside the virtual desktop (converted to the window's DIP space).
    /// Monitor enumeration mirrors <see cref="WgcScreenCaptureService"/> (primary
    /// first) so index <c>i</c> here captures the same display the service does.
    /// </summary>
    private async Task ComposeBackdropAsync(IScreenCaptureService capture)
    {
        List<RECT> monitors = EnumerateMonitorBounds();
        for (int i = 0; i < monitors.Count; i++)
        {
            Result<CapturedImage> shot = await capture.CaptureMonitorAsync(i);
            if (!shot.IsSuccess)
            {
                continue;
            }

            RECT bounds = monitors[i];
            var image = new Image
            {
                Source = ImageBridge.ToWriteableBitmap(shot.Value),
                Stretch = Stretch.Fill,
                Width = (bounds.right - bounds.left) / _scale,
                Height = (bounds.bottom - bounds.top) / _scale,
            };

            // Physical virtual-desktop origin -> window-local DIPs.
            Canvas.SetLeft(image, (bounds.left - _virtualLeft) / _scale);
            Canvas.SetTop(image, (bounds.top - _virtualTop) / _scale);
            BackdropCanvas.Children.Add(image);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _start = e.GetCurrentPoint(SelectionCanvas).Position;
        _dragging = true;

        Canvas.SetLeft(SelectionRect, _start.X);
        Canvas.SetTop(SelectionRect, _start.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;

        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Point current = e.GetCurrentPoint(SelectionCanvas).Position;
        double x = Math.Min(_start.X, current.X);
        double y = Math.Min(_start.Y, current.Y);
        double w = Math.Abs(current.X - _start.X);
        double h = Math.Abs(current.Y - _start.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        double x = Canvas.GetLeft(SelectionRect);
        double y = Canvas.GetTop(SelectionRect);
        double w = SelectionRect.Width;
        double h = SelectionRect.Height;

        if (w < 4 || h < 4)
        {
            Complete(null); // a click, not a drag
            return;
        }

        // DIP selection -> physical pixels at the window's scale, then translate by the
        // virtual-desktop origin so the rect is in virtual-desktop physical coordinates
        // (which is where CaptureRegionAsync resolves the monitor + crops).
        PhysicalRect local = new LogicalRect(x, y, w, h).ToPhysical(new Dpi(_scale * 96.0));
        Complete(new PhysicalRect(_virtualLeft + local.X, _virtualTop + local.Y, local.Width, local.Height));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Complete(null);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args) => _result.TrySetResult(null);

    private void Complete(PhysicalRect? region)
    {
        _result.TrySetResult(region);
        if (!_closed)
        {
            _closed = true;
            Close();
        }
    }

    /// <summary>
    /// Monitor bounds in virtual-desktop physical pixels, primary first — identical
    /// ordering to <see cref="WgcScreenCaptureService"/> so capture indices line up.
    /// </summary>
    private static List<RECT> EnumerateMonitorBounds()
    {
        var monitors = new List<(RECT Bounds, bool Primary)>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                bool primary = (info.dwFlags & MonitorInfofPrimary) != 0;
                monitors.Add((info.rcMonitor, primary));
            }

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        monitors.Sort((a, b) => b.Primary.CompareTo(a.Primary));

        var bounds = new List<RECT>(monitors.Count);
        foreach ((RECT rect, bool _) in monitors)
        {
            bounds.Add(rect);
        }

        return bounds;
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
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
