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
using Windows.Graphics;

namespace Snaply;

/// <summary>
/// A borderless, always-on-top overlay covering the entire <em>virtual desktop</em>
/// (every monitor). It freezes a screenshot of each monitor as its backdrop, then
/// highlights whichever enumerated top-level window sits under the cursor (CleanShot /
/// Xnapper style). Clicking a highlighted window returns its native handle (HWND);
/// clicking empty space or pressing Esc cancels (returns <c>null</c>).
/// </summary>
public sealed partial class WindowPickerOverlay : Window
{
    // Virtual-screen metrics (bounding box of all monitors, physical pixels for a
    // per-monitor-DPI-aware process).
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int MonitorInfofPrimary = 0x1;

    private readonly TaskCompletionSource<nint?> _result = new();
    private readonly List<WindowInfo> _windows = [];

    private double _scale = 1.0;
    private int _virtualLeft;
    private int _virtualTop;
    private WindowInfo? _hovered;
    private nint _ownHandle;
    private bool _closed;

    /// <summary>Creates the overlay window and hooks its close handler.</summary>
    public WindowPickerOverlay()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    /// <summary>Show the overlay and await the window the user clicks.</summary>
    /// <param name="enumerator">Enumerates the top-level windows to offer, topmost first.</param>
    /// <param name="capture">Capture service used to freeze each monitor as the backdrop.</param>
    /// <returns>The picked window's native handle (HWND), or <c>null</c> on cancel.</returns>
    public async Task<nint?> PickWindowAsync(IWindowEnumerationService enumerator, IScreenCaptureService capture)
    {
        ConfigureWindow();

        _ownHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _scale = DpiProvider.GetForWindow(_ownHandle).Scale;

        // Freeze each monitor as the backdrop so the user picks against what they see.
        await ComposeBackdropAsync(capture);

        // Snapshot the pickable windows once (topmost first), excluding this overlay.
        _windows.Clear();
        foreach (WindowInfo window in enumerator.EnumerateTopLevelWindows())
        {
            if (window.Handle != _ownHandle)
            {
                _windows.Add(window);
            }
        }

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

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point pos = e.GetCurrentPoint(RootGrid).Position;

        // Overlay DIP -> physical virtual-desktop pixels (the space WindowInfo.Bounds is in).
        double px = _virtualLeft + (pos.X * _scale);
        double py = _virtualTop + (pos.Y * _scale);

        WindowInfo? hit = null;
        foreach (WindowInfo window in _windows)
        {
            if (Contains(window.Bounds, px, py))
            {
                hit = window; // _windows is topmost-first, so the first match is frontmost
                break;
            }
        }

        if (ReferenceEquals(hit, _hovered))
        {
            return;
        }

        _hovered = hit;
        if (hit is null)
        {
            HideHighlight();
        }
        else
        {
            ShowHighlight(hit);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) =>
        RootGrid.CapturePointer(e.Pointer);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.ReleasePointerCapture(e.Pointer);
        Complete(_hovered?.Handle);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Complete(null);
        }
    }

    private void ShowHighlight(WindowInfo window)
    {
        // Physical virtual-desktop rect -> overlay-local DIPs.
        double x = (window.Bounds.X - _virtualLeft) / _scale;
        double y = (window.Bounds.Y - _virtualTop) / _scale;
        double w = window.Bounds.Width / _scale;
        double h = window.Bounds.Height / _scale;

        Canvas.SetLeft(HighlightRect, x);
        Canvas.SetTop(HighlightRect, y);
        HighlightRect.Width = w;
        HighlightRect.Height = h;
        HighlightRect.Visibility = Visibility.Visible;

        TitleText.Text = window.Title;
        TitleText.MaxWidth = Math.Max(0, w);
        Canvas.SetLeft(TitleBadge, x);
        Canvas.SetTop(TitleBadge, y);
        TitleBadge.Visibility = Visibility.Visible;
    }

    private void HideHighlight()
    {
        HighlightRect.Visibility = Visibility.Collapsed;
        TitleBadge.Visibility = Visibility.Collapsed;
    }

    private static bool Contains(PhysicalRect bounds, double px, double py) =>
        px >= bounds.X && px < bounds.X + bounds.Width && py >= bounds.Y && py < bounds.Y + bounds.Height;

    private void OnClosed(object sender, WindowEventArgs args) => _result.TrySetResult(null);

    private void Complete(nint? handle)
    {
        _result.TrySetResult(handle);
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
