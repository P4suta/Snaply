using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace Snaply.Controls;

internal sealed partial class ZoomableImage : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ImageSource),
        typeof(ZoomableImage),
        new PropertyMetadata(null, OnSourceChanged));

    private const float ZoomStep = 1.25f;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _keepFitted = true;

    public ZoomableImage()
    {
        InitializeComponent();
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ZoomableImage)sender;
        control.DisplayImage.Source = args.NewValue as ImageSource;
        control.ZoomToolbar.Visibility = args.NewValue is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        control.Scroller.IsTabStop = args.NewValue is not null;
        control._keepFitted = true;
        control.DispatcherQueue.TryEnqueue(control.Fit);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs args) => ZoomBy(ZoomStep);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs args) => ZoomBy(1 / ZoomStep);

    private void FitButton_Click(object sender, RoutedEventArgs args) => Fit();

    private void ActualSizeButton_Click(object sender, RoutedEventArgs args) => SetZoom(1, keepFitted: false);

    private void Scroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        Fit();
        args.Handled = true;
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_keepFitted)
        {
            Fit();
        }
    }

    private void DisplayImage_ImageOpened(object sender, RoutedEventArgs args)
    {
        if (_keepFitted)
        {
            Fit();
        }
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        ZoomLevelText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0:P0}",
            Scroller.ZoomFactor);
    }

    private void ZoomInAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomBy(ZoomStep);
        args.Handled = true;
    }

    private void ZoomOutAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomBy(1 / ZoomStep);
        args.Handled = true;
    }

    private void FitAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        Fit();
        args.Handled = true;
    }

    private void ActualSizeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        SetZoom(1, keepFitted: false);
        args.Handled = true;
    }

    private void ZoomBy(float factor)
    {
        if (Source is null)
        {
            return;
        }

        SetZoom(Scroller.ZoomFactor * factor, keepFitted: false);
    }

    private void Fit()
    {
        if (Source is not BitmapSource bitmap
            || bitmap.PixelWidth <= 0
            || bitmap.PixelHeight <= 0
            || Scroller.ViewportWidth <= 0
            || Scroller.ViewportHeight <= 0)
        {
            return;
        }

        float fit = (float)Math.Min(
            1,
            Math.Min(
                Scroller.ViewportWidth / bitmap.PixelWidth,
                Scroller.ViewportHeight / bitmap.PixelHeight));
        SetZoom(fit, keepFitted: true);
    }

    private void SetZoom(float zoom, bool keepFitted)
    {
        if (Source is null)
        {
            return;
        }

        _keepFitted = keepFitted;
        float clamped = Math.Clamp(zoom, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        _ = Scroller.ChangeView(
            horizontalOffset: null,
            verticalOffset: null,
            zoomFactor: clamped,
            disableAnimation: !_animationsEnabled);
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = Scroller.ChangeView(
                Scroller.ScrollableWidth / 2,
                Scroller.ScrollableHeight / 2,
                zoomFactor: null,
                disableAnimation: true);
        });
    }
}
