using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Snaply.Controls;

/// <summary>
/// A self-contained zoom/pan image viewer. The image is laid out <see cref="Stretch.Uniform"/>
/// so at scale <c>1.0</c> it already fits the viewport (the "fit" baseline); the element's
/// Composition <see cref="Visual.Scale"/>/<see cref="Visual.Offset"/> then scale/translate from
/// there. Mouse-wheel zooms about the cursor, left-drag pans, double-tap resets to fit, and a
/// floating bar offers Fit / 100% buttons plus a live zoom-percent label.
/// </summary>
/// <remarks>
/// Zoom (wheel, Fit, 100%, double-tap) glides via GPU-composited key-frame animations with a
/// cubic ease-out, so rapid consecutive notches retarget smoothly from the current in-flight
/// value instead of snapping. Panning writes the offset directly so the drag stays 1:1 with the
/// cursor. The <c>_scale</c>/<c>_translateX</c>/<c>_translateY</c> fields always hold the
/// <em>target</em> state, so the cursor-anchor math stays pixel-correct at rest.
/// </remarks>
public sealed partial class ZoomableImage : UserControl
{
    /// <summary>Identifies the <see cref="Source"/> dependency property.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ImageSource),
        typeof(ZoomableImage),
        new PropertyMetadata(null, OnSourceChanged));

    // Zoom limits and per-notch multiplier. Scale is relative to the fit baseline (1.0).
    private const double MinScale = 0.1;
    private const double MaxScale = 8.0;
    private const double ZoomStep = 1.1;

    // Spring motion for zoom. A spring continuously chases its FinalValue, so retargeting it
    // on every wheel notch produces one uninterrupted, momentum-preserving glide (no per-notch
    // ease-out "pulsing"). Critically damped (no overshoot); a short period keeps it snappy.
    private const float SpringDamping = 1.0f;
    private static readonly TimeSpan SpringPeriod = TimeSpan.FromMilliseconds(45);

    // Hand cursors are app-lifetime singletons (static so the control needs no IDisposable).
    private static readonly InputCursor PanCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    private readonly Visual _imageVisual;
    private readonly Compositor _compositor;
    private readonly SpringVector3NaturalMotionAnimation _scaleSpring;
    private readonly SpringVector3NaturalMotionAnimation _offsetSpring;

    private double _scale = 1.0;
    private double _translateX;
    private double _translateY;
    private double _naturalWidth;
    private double _naturalHeight;
    private bool _isPanning;
    private Point _lastPoint;

    /// <summary>Creates the control and wires up the Composition visual used for zoom/pan.</summary>
    public ZoomableImage()
    {
        InitializeComponent();
        ProtectedCursor = ArrowCursor;

        // Drive Scale/Offset on the image's backing Composition visual. Composition runs on the
        // compositor thread, synced to the display's refresh (60/120/144Hz+), so motion stays
        // smooth regardless of UI-thread load.
        _imageVisual = ElementCompositionPreview.GetElementVisual(DisplayImage);
        _compositor = _imageVisual.Compositor;

        // Pan via the composition "Translation" property (composed ON TOP of the layout-owned
        // Offset), never by writing Offset directly — that fights layout and drifts the image out
        // of centre after repeated Source changes (each capture re-lays-out the element).
        ElementCompositionPreview.SetIsTranslationEnabled(DisplayImage, true);

        _scaleSpring = _compositor.CreateSpringVector3Animation();
        _scaleSpring.DampingRatio = SpringDamping;
        _scaleSpring.Period = SpringPeriod;

        _offsetSpring = _compositor.CreateSpringVector3Animation();
        _offsetSpring.DampingRatio = SpringDamping;
        _offsetSpring.Period = SpringPeriod;
    }

    /// <summary>The image to display. Setting a new image auto-fits it to the viewport.</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ZoomableImage)d;
        var source = e.NewValue as ImageSource;
        control.DisplayImage.Source = source;

        if (source is BitmapSource bitmap)
        {
            control._naturalWidth = bitmap.PixelWidth;
            control._naturalHeight = bitmap.PixelHeight;
        }
        else
        {
            control._naturalWidth = 0;
            control._naturalHeight = 0;
        }

        control.ZoomControlsBar.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;

        // A brand-new image should appear fitted immediately, not glide in from the old view.
        control.ResetToFit(animate: false);
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Clip the (transformed) image to the viewport so panned/zoomed pixels never
        // spill over the neighbouring panels.
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight) };
        UpdateLabel();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(Viewport);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        double newScale = Math.Clamp(_scale * (delta > 0 ? ZoomStep : 1.0 / ZoomStep), MinScale, MaxScale);
        ZoomAbout(point.Position, newScale);
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(Viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _lastPoint = point.Position;

        // Snap to the current target so a drag begun mid-glide stays 1:1 from the first move.
        ApplyTransformInstant();
        Viewport.CapturePointer(e.Pointer);
        ProtectedCursor = PanCursor;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        Point position = e.GetCurrentPoint(Viewport).Position;
        _translateX += position.X - _lastPoint.X;
        _translateY += position.Y - _lastPoint.Y;
        _lastPoint = position;

        // Direct write (no animation) keeps the drag glued to the cursor.
        ApplyTransformInstant();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        Viewport.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = ArrowCursor;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResetToFit(animate: true);

    private void OnFitClick(object sender, RoutedEventArgs e) => ResetToFit(animate: true);

    private void OnHundredClick(object sender, RoutedEventArgs e)
    {
        double target = Math.Clamp(HundredPercentScale(), MinScale, MaxScale);
        ZoomAbout(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), target);
    }

    /// <summary>Scale to <paramref name="newScale"/> while keeping <paramref name="anchor"/> fixed on screen.</summary>
    private void ZoomAbout(Point anchor, double newScale)
    {
        if (newScale <= 0)
        {
            return;
        }

        double factor = newScale / _scale;
        _translateX = anchor.X - (factor * (anchor.X - _translateX));
        _translateY = anchor.Y - (factor * (anchor.Y - _translateY));
        _scale = newScale;
        ApplyTransformAnimated();
    }

    private void ResetToFit(bool animate)
    {
        _scale = 1.0;
        _translateX = 0;
        _translateY = 0;

        if (animate)
        {
            ApplyTransformAnimated();
        }
        else
        {
            ApplyTransformInstant();
        }
    }

    /// <summary>
    /// Spring the visual toward the current target scale/offset. Re-invoking a spring retargets it
    /// from the current in-flight value, so rapid wheel notches chain into one continuous,
    /// compositor-driven glide (no per-notch ease-out pulsing).
    /// </summary>
    private void ApplyTransformAnimated()
    {
        ClampTranslation();
        _scaleSpring.FinalValue = new Vector3((float)_scale, (float)_scale, 1f);
        _offsetSpring.FinalValue = new Vector3((float)_translateX, (float)_translateY, 0f);
        _imageVisual.StartAnimation("Scale", _scaleSpring);
        _imageVisual.StartAnimation("Translation", _offsetSpring);
        UpdateLabel();
    }

    /// <summary>Write the current target scale/offset immediately (used for panning and new-image fit).</summary>
    private void ApplyTransformInstant()
    {
        ClampTranslation();

        // A direct property set stops any in-flight animation on that property, then holds.
        _imageVisual.Scale = new Vector3((float)_scale, (float)_scale, 1f);
        _imageVisual.Properties.InsertVector3("Translation", new Vector3((float)_translateX, (float)_translateY, 0f));
        UpdateLabel();
    }

    // Keep the (scaled) image overlapping the viewport so it can never be shoved out of sight.
    // The image element fills the viewport at scale 1 and the visual scales from its top-left, so
    // the element spans [translate, translate + viewport*scale] on each axis.
    private void ClampTranslation()
    {
        _translateX = ClampAxis(_translateX, Viewport.ActualWidth, _scale);
        _translateY = ClampAxis(_translateY, Viewport.ActualHeight, _scale);
    }

    private static double ClampAxis(double translate, double viewport, double scale)
    {
        if (viewport <= 0)
        {
            return translate;
        }

        double scaled = viewport * scale;

        // Zoomed in: no empty gap at the edges. Zoomed out: stay fully within the viewport.
        return scaled >= viewport
            ? Math.Clamp(translate, viewport - scaled, 0.0)
            : Math.Clamp(translate, 0.0, viewport - scaled);
    }

    /// <summary>
    /// The scale at which one image pixel maps to one physical screen pixel. Fit displays the
    /// image at <c>fitFactor</c> DIPs per image-pixel; multiplying by the rasterization scale
    /// converts DIPs to physical pixels, so actual-pixels is <c>1 / (fitFactor * rasterizationScale)</c>.
    /// Falls back to the fit baseline when the natural size or viewport is not yet known.
    /// </summary>
    private double HundredPercentScale()
    {
        double viewportWidth = Viewport.ActualWidth;
        double viewportHeight = Viewport.ActualHeight;
        if (_naturalWidth <= 0 || _naturalHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return 1.0;
        }

        double fitFactor = Math.Min(viewportWidth / _naturalWidth, viewportHeight / _naturalHeight);
        double raster = XamlRoot?.RasterizationScale ?? 1.0;
        double actual = fitFactor * raster;
        return actual > 0 ? 1.0 / actual : 1.0;
    }

    private void UpdateLabel()
    {
        double hundred = HundredPercentScale();
        double percent = hundred > 0 ? _scale / hundred * 100.0 : _scale * 100.0;
        ZoomLabel.Text = string.Create(CultureInfo.CurrentCulture, $"{percent:0}%");
    }
}
