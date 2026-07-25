using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Snaply.Controls;
using Snaply.Imaging;
using Windows.Foundation;
using Windows.Graphics;

namespace Snaply;

internal sealed partial class RegionSelectionService(Action activateOwner) : IDisposable
{
    private readonly Action _activateOwner = activateOwner;
    private readonly Dictionary<nint, RegionSelectionWindow> _windows = [];
    private bool _disposed;

    internal async Task<PixelRect?> PickAsync(
        IReadOnlyList<MonitorSnapshot> monitors,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<RegionSelectionWindow> windows = GetWindows(monitors);
        var controller = new RegionSelectionController(
            windows,
            _activateOwner,
            cancellationToken);
        return await controller.RunAsync();
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
            window.ClosePermanently();
        }

        _windows.Clear();
    }

    private List<RegionSelectionWindow> GetWindows(IReadOnlyList<MonitorSnapshot> monitors)
    {
        var activeHandles = monitors.Select(static monitor => monitor.Handle).ToHashSet();
        foreach (nint handle in _windows.Keys.Where(handle => !activeHandles.Contains(handle)).ToArray())
        {
            _windows[handle].ClosePermanently();
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
        private readonly Action _activateOwner;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly TaskCompletionSource<PixelRect?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        private readonly IReadOnlyList<RegionSelectionWindow> _windows;
        private PixelPoint _start;
        private bool _dragging;
        private bool _finished;

        internal RegionSelectionController(
            IReadOnlyList<RegionSelectionWindow> windows,
            Action activateOwner,
            CancellationToken cancellationToken)
        {
            _windows = windows;
            _activateOwner = activateOwner;
            _cancellationRegistration = cancellationToken.Register(
                () => _dispatcher.TryEnqueue(Cancel));
        }

        internal async Task<PixelRect?> RunAsync()
        {
            try
            {
                foreach (RegionSelectionWindow window in _windows)
                {
                    window.BeginSelection(BeginDrag, UpdateDrag, Complete, Cancel);
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
            UpdateSelection(start);
        }

        private void UpdateDrag(PixelPoint current)
        {
            if (_dragging)
            {
                UpdateSelection(current);
            }
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
            Finish(selection.Width < 2 || selection.Height < 2 ? null : selection);
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
            int failure = 0;
            foreach (RegionSelectionWindow window in _windows)
            {
                int resultCode = window.EndSelection();
                if (resultCode < 0 && failure == 0)
                {
                    failure = resultCode;
                }
            }

            _activateOwner();
            if (failure < 0)
            {
                _completion.TrySetException(Marshal.GetExceptionForHR(failure)!);
            }
            else
            {
                _completion.TrySetResult(result);
            }
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
        private readonly RegionSelectionOverlay _overlay = new();
        private Action<PixelPoint>? _beginDrag;
        private Action? _cancel;
        private Action<PixelPoint>? _complete;
        private bool _isClosingPermanently;
        private bool _isPositioned;
        private MonitorSnapshot _monitor = null!;
        private PixelRect _positionedBounds;
        private Action<PixelPoint>? _updateDrag;

        internal RegionSelectionWindow()
        {
            Content = _overlay;
            _overlay.DragStarted += point => _beginDrag?.Invoke(ToScreenPoint(point));
            _overlay.DragMoved += point => _updateDrag?.Invoke(ToScreenPoint(point));
            _overlay.DragCompleted += point => _complete?.Invoke(ToScreenPoint(point));
            _overlay.Cancelled += () => _cancel?.Invoke();

            AppWindow.Closing += (_, args) =>
            {
                if (!_isClosingPermanently && _cancel is not null)
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

            _overlay.Begin();
            AppWindow.Show();
            Activate();
            _overlay.Focus(FocusState.Programmatic);
        }

        internal int EndSelection()
        {
            _overlay.End();
            _beginDrag = null;
            _updateDrag = null;
            _complete = null;
            _cancel = null;
            AppWindow.Hide();
            return DwmFlush();
        }

        internal void SetSelection(PixelRect screenSelection)
        {
            PixelRect local = screenSelection.Intersect(_monitor.Bounds);
            if (local.IsEmpty || Content is not FrameworkElement root)
            {
                _overlay.SetSelection(default);
                return;
            }

            double scale = root.XamlRoot?.RasterizationScale ?? 1;
            _overlay.SetSelection(new Rect(
                (local.X - _monitor.Bounds.X) / scale,
                (local.Y - _monitor.Bounds.Y) / scale,
                local.Width / scale,
                local.Height / scale));
        }

        internal void ClosePermanently()
        {
            if (_isClosingPermanently)
            {
                return;
            }

            _isClosingPermanently = true;
            if (_cancel is not null)
            {
                _cancel();
            }
            else
            {
                _ = EndSelection();
            }

            Close();
        }

        private PixelPoint ToScreenPoint(Point position)
        {
            double scale = _overlay.XamlRoot?.RasterizationScale ?? 1;
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
    }

    private readonly record struct PixelPoint(int X, int Y);
}
