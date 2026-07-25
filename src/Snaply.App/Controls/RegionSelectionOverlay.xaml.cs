using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Snaply.Controls;

internal sealed partial class RegionSelectionOverlay : UserControl
{
    private uint? _activePointerId;

    internal RegionSelectionOverlay()
    {
        InitializeComponent();
    }

    internal event Action<Point>? DragStarted;

    internal event Action<Point>? DragMoved;

    internal event Action<Point>? DragCompleted;

    internal event Action? Cancelled;

    internal void Begin()
    {
        SelectionVisual.Visibility = Visibility.Collapsed;
        Focus(FocusState.Programmatic);
    }

    internal void End()
    {
        _activePointerId = null;
        InputSurface.ReleasePointerCaptures();
        SelectionVisual.Visibility = Visibility.Collapsed;
    }

    internal void SetSelection(Rect selection)
    {
        if (selection.Width <= 0 || selection.Height <= 0)
        {
            SelectionVisual.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(SelectionVisual, selection.X);
        Canvas.SetTop(SelectionVisual, selection.Y);
        SelectionVisual.Width = selection.Width;
        SelectionVisual.Height = selection.Height;
        SelectionVisual.Visibility = Visibility.Visible;
    }

    private void InputSurface_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId is not null)
        {
            return;
        }

        PointerPoint point = args.GetCurrentPoint(InputSurface);
        if (!point.Properties.IsLeftButtonPressed
            && args.Pointer.PointerDeviceType is not (
                PointerDeviceType.Touch
                or PointerDeviceType.Pen))
        {
            return;
        }

        if (!InputSurface.CapturePointer(args.Pointer))
        {
            return;
        }

        _activePointerId = args.Pointer.PointerId;
        DragStarted?.Invoke(point.Position);
        args.Handled = true;
    }

    private void InputSurface_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId != args.Pointer.PointerId)
        {
            return;
        }

        DragMoved?.Invoke(args.GetCurrentPoint(InputSurface).Position);
        args.Handled = true;
    }

    private void InputSurface_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId != args.Pointer.PointerId)
        {
            return;
        }

        Point position = args.GetCurrentPoint(InputSurface).Position;
        _activePointerId = null;
        if (InputSurface.PointerCaptures.Contains(args.Pointer))
        {
            InputSurface.ReleasePointerCapture(args.Pointer);
        }

        DragCompleted?.Invoke(position);
        args.Handled = true;
    }

    private void InputSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId == args.Pointer.PointerId)
        {
            _activePointerId = null;
            Cancelled?.Invoke();
            args.Handled = true;
        }
    }

    private void InputSurface_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId != args.Pointer.PointerId)
        {
            return;
        }

        _activePointerId = null;
        if (InputSurface.PointerCaptures.Contains(args.Pointer))
        {
            InputSurface.ReleasePointerCapture(args.Pointer);
        }

        Cancelled?.Invoke();
        args.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs args) => Cancelled?.Invoke();

    private void Escape_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        Cancelled?.Invoke();
        args.Handled = true;
    }
}
