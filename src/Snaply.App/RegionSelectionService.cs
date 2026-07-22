using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Snaply.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Snaply;

internal sealed partial class RegionSelectionService : IDisposable
{
    private readonly Dictionary<nint, RegionSelectionWindow> _windows = [];
    private bool _disposed;

    internal async Task<PixelRect?> PickAsync(
        IReadOnlyList<MonitorSnapshot> monitors,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<RegionSelectionWindow> windows = GetWindows(monitors);
        var controller = new RegionSelectionController(windows, cancellationToken);
        PixelRect? result = await controller.RunAsync();
        if (result is not null)
        {
            await Task.Delay(75, cancellationToken);
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (RegionSelectionWindow window in _windows.Values)
        {
            window.Close();
        }

        _windows.Clear();
    }

    private List<RegionSelectionWindow> GetWindows(
        IReadOnlyList<MonitorSnapshot> monitors)
    {
        var activeHandles = monitors.Select(static monitor => monitor.Handle).ToHashSet();
        foreach (nint handle in _windows.Keys.Where(handle => !activeHandles.Contains(handle)).ToArray())
        {
            _windows[handle].Close();
            _windows.Remove(handle);
        }

        var result = new List<RegionSelectionWindow>(monitors.Count);
        foreach (MonitorSnapshot monitor in monitors)
        {
            if (!_windows.TryGetValue(monitor.Handle, out RegionSelectionWindow? window))
            {
                window = new RegionSelectionWindow();
                _windows.Add(monitor.Handle, window);
            }

            window.SetMonitor(monitor);
            result.Add(window);
        }

        return result;
    }

    private sealed class RegionSelectionController
    {
        private readonly TaskCompletionSource<PixelRect?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly IReadOnlyList<RegionSelectionWindow> _windows;
        private PixelPoint _start;
        private bool _dragging;
        private bool _finished;

        internal RegionSelectionController(
            IReadOnlyList<RegionSelectionWindow> windows,
            CancellationToken cancellationToken)
        {
            _windows = windows;
            _cancellationRegistration = cancellationToken.Register(
                () => _dispatcher.TryEnqueue(Cancel));
        }

        internal async Task<PixelRect?> RunAsync()
        {
            try
            {
                foreach (RegionSelectionWindow window in _windows)
                {
                    window.BeginSelection(
                        BeginDrag,
                        UpdateDrag,
                        Complete,
                        Cancel);
                }

                return await _completion.Task;
            }
            catch
            {
                Finish(null);
                throw;
            }
        }

        private void BeginDrag(PixelPoint start)
        {
            _start = start;
            _dragging = true;
            UpdateSelection(_start);
        }

        private void UpdateDrag(PixelPoint current)
        {
            if (!_dragging)
            {
                return;
            }

            UpdateSelection(current);
        }

        private void UpdateSelection(PixelPoint current)
        {
            PixelRect selection = CreateSelection(_start, current);
            foreach (RegionSelectionWindow window in _windows)
            {
                window.SetSelection(selection);
            }
        }

        private void Complete(PixelPoint end)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            PixelRect selection = CreateSelection(_start, end);
            if (selection.Width < 2 || selection.Height < 2)
            {
                Cancel();
                return;
            }

            Finish(selection);
        }

        private void Cancel() => Finish(null);

        private void Finish(PixelRect? result)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _cancellationRegistration.Dispose();
            foreach (RegionSelectionWindow window in _windows)
            {
                window.EndSelection();
            }

            App.MainWindow.Activate();
            _completion.TrySetResult(result);
        }

        private static PixelRect CreateSelection(PixelPoint first, PixelPoint second)
        {
            int left = Math.Min(first.X, second.X);
            int top = Math.Min(first.Y, second.Y);
            int right = Math.Max(first.X, second.X);
            int bottom = Math.Max(first.Y, second.Y);
            return new PixelRect(left, top, checked(right - left), checked(bottom - top));
        }
    }

    private sealed partial class RegionSelectionWindow : Window
    {
        private const uint WdaExcludeFromCapture = 0x00000011;
        private readonly Canvas _canvas;
        private readonly Rectangle _selection;
        private MonitorSnapshot _monitor = null!;
        private PixelRect _positionedBounds;
        private Action<PixelPoint>? _beginDrag;
        private Action<PixelPoint>? _updateDrag;
        private Action<PixelPoint>? _complete;
        private Action? _cancel;
        private PixelPoint _lastPointerPosition;
        private PixelPoint _pointerStartPosition;
        private bool _isPositioned;
        private bool _isSelecting;

        internal RegionSelectionWindow()
        {
            _canvas = new Canvas
            {
                Background = new SolidColorBrush(Color.FromArgb(112, 0, 0, 0)),
                IsTabStop = true,
            };
            _selection = new Rectangle
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_selection);
            var hint = new TextBlock
            {
                Text = ResourceText.Get("RegionHint"),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 16,
                Padding = new Thickness(12, 8, 12, 8),
            };
            Canvas.SetLeft(hint, 20);
            Canvas.SetTop(hint, 20);
            _canvas.Children.Add(hint);
            var cancel = new Button
            {
                Content = ResourceText.Get("RegionCancel"),
                Padding = new Thickness(12, 8, 12, 8),
            };
            AutomationProperties.SetAutomationId(cancel, "RegionCancelButton");
            cancel.Click += (_, _) => _cancel?.Invoke();
            Canvas.SetLeft(cancel, 20);
            Canvas.SetTop(cancel, 72);
            _canvas.Children.Add(cancel);
            _canvas.PointerPressed += (_, args) =>
            {
                PointerPoint point = args.GetCurrentPoint(_canvas);
                if (point.Properties.IsLeftButtonPressed
                    || args.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
                {
                    _isSelecting = true;
                    _canvas.CapturePointer(args.Pointer);
                    _lastPointerPosition = ToScreenPoint(point.Position);
                    _pointerStartPosition = _lastPointerPosition;
                    _beginDrag?.Invoke(_lastPointerPosition);
                    args.Handled = true;
                }
            };
            _canvas.PointerMoved += (_, args) =>
            {
                if (_isSelecting)
                {
                    _lastPointerPosition = ToScreenPoint(args.GetCurrentPoint(_canvas).Position);
                    _updateDrag?.Invoke(_lastPointerPosition);
                    args.Handled = true;
                }
            };
            _canvas.PointerReleased += (_, args) =>
            {
                if (_isSelecting)
                {
                    _lastPointerPosition = ToScreenPoint(args.GetCurrentPoint(_canvas).Position);
                    _isSelecting = false;
                    if (_canvas.PointerCaptures.Contains(args.Pointer))
                    {
                        _canvas.ReleasePointerCapture(args.Pointer);
                    }

                    _complete?.Invoke(_lastPointerPosition);
                    args.Handled = true;
                }
            };
            _canvas.PointerCaptureLost += (_, _) =>
            {
                if (_isSelecting && _lastPointerPosition != _pointerStartPosition)
                {
                    _isSelecting = false;
                    _complete?.Invoke(_lastPointerPosition);
                }
            };
            var escape = new KeyboardAccelerator
            {
                Key = VirtualKey.Escape,
            };
            escape.Invoked += (_, args) =>
            {
                _cancel?.Invoke();
                args.Handled = true;
            };
            _canvas.KeyboardAccelerators.Add(escape);
            Content = _canvas;
            AppWindow.Closing += (_, args) =>
            {
                if (_cancel is not null)
                {
                    args.Cancel = true;
                    _cancel();
                }
            };
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
            }

            _ = SetWindowDisplayAffinity(
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                WdaExcludeFromCapture);
            AppWindow.IsShownInSwitchers = false;
        }

        internal void SetMonitor(MonitorSnapshot monitor) => _monitor = monitor;

        internal void BeginSelection(
            Action<PixelPoint> beginDrag,
            Action<PixelPoint> updateDrag,
            Action<PixelPoint> complete,
            Action cancel)
        {
            _beginDrag = beginDrag;
            _updateDrag = updateDrag;
            _complete = complete;
            _cancel = cancel;
            _selection.Visibility = Visibility.Collapsed;
            if (!_isPositioned || _positionedBounds != _monitor.Bounds)
            {
                AppWindow.MoveAndResize(new RectInt32(
                    _monitor.Bounds.X,
                    _monitor.Bounds.Y,
                    _monitor.Bounds.Width,
                    _monitor.Bounds.Height));
                _positionedBounds = _monitor.Bounds;
                _isPositioned = true;
            }

            AppWindow.Show();
            Activate();
            _canvas.Focus(FocusState.Programmatic);
        }

        internal void EndSelection()
        {
            _isSelecting = false;
            _canvas.ReleasePointerCaptures();
            _selection.Visibility = Visibility.Collapsed;
            _beginDrag = null;
            _updateDrag = null;
            _complete = null;
            _cancel = null;
            AppWindow.Hide();
            _ = DwmFlush();
        }

        internal void SetSelection(PixelRect screenSelection)
        {
            PixelRect local = screenSelection.Intersect(_monitor.Bounds);
            if (local.IsEmpty || Content is not FrameworkElement root)
            {
                _selection.Visibility = Visibility.Collapsed;
                return;
            }

            double scale = root.XamlRoot?.RasterizationScale ?? 1;
            Canvas.SetLeft(_selection, (local.X - _monitor.Bounds.X) / scale);
            Canvas.SetTop(_selection, (local.Y - _monitor.Bounds.Y) / scale);
            _selection.Width = local.Width / scale;
            _selection.Height = local.Height / scale;
            _selection.Visibility = Visibility.Visible;
        }

        private PixelPoint ToScreenPoint(Point position)
        {
            double scale = _canvas.XamlRoot?.RasterizationScale ?? 1;
            return new PixelPoint(
                checked(_monitor.Bounds.X + (int)Math.Round(
                    position.X * scale,
                    MidpointRounding.AwayFromZero)),
                checked(_monitor.Bounds.Y + (int)Math.Round(
                    position.Y * scale,
                    MidpointRounding.AwayFromZero)));
        }

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmFlush();

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);
    }

    private readonly record struct PixelPoint(int X, int Y);
}
