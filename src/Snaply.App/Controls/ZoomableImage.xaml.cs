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
/// floating fit-to-view button (bottom-right) resets the zoom.
/// </summary>
/// <remarks>
/// Zoom (wheel, Fit, double-tap) glides via GPU-composited spring animations, so rapid consecutive
/// notches retarget smoothly from the current in-flight value instead of snapping. Panning writes
/// the offset directly so the drag stays 1:1 with the cursor. The <c>_scale</c>/<c>_translateX</c>/
/// <c>_translateY</c> fields always hold the <em>target</em> state, so the cursor-anchor math stays
/// pixel-correct at rest.
/// </remarks>
internal sealed partial class ZoomableImage : UserControl
{
    /// <summary>Identifies the <see cref="Source"/> dependency property.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ImageSource),
        typeof(ZoomableImage),
        new PropertyMetadata(null, OnSourceChanged));

    // Zoom limits and per-notch multiplier. Scale is relative to the fit baseline (1.0), which is
    // also the floor: Fit is as small as it gets — the wheel only zooms in, never below Fit.
    private const double MinScale = 1.0;
    private const double MaxScale = 8.0;
    private const double ZoomStep = 1.1;

    // Z translation kept on the image at all times so its ThemeShadow reads as a floating lift over
    // the ambient backdrop (carried in the composition Translation alongside the pan offset).
    private const float ShadowDepth = 32f;

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
    private bool _isPanning;
    private Point _lastPoint;

    /// <summary>Creates the control and wires up the Composition visual used for zoom/pan.</summary>
    public ZoomableImage()
    {
        InitializeComponent();
        ProtectedCursor = ArrowCursor;

        // Empty until a capture arrives: stay out of the pointer's way (no pan cursor / no zoom) so
        // the empty canvas — and the ambient backdrop showing through — reads as inert, not a viewer.
        Viewport.IsHitTestVisible = false;

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

        // Zoom/pan only make sense once there's an image; while empty the viewer is inert so it
        // never shows a pan cursor over the ambient backdrop.
        control.Viewport.IsHitTestVisible = source is not null;

        // Never upscale: cap the element at the image's native pixel size so a capture smaller than
        // the viewport shows at 100% (centred) rather than being blown up; larger captures still fit.
        if (source is BitmapSource bitmap)
        {
            control.DisplayImage.MaxWidth = bitmap.PixelWidth;
            control.DisplayImage.MaxHeight = bitmap.PixelHeight;
        }
        else
        {
            control.DisplayImage.MaxWidth = double.PositiveInfinity;
            control.DisplayImage.MaxHeight = double.PositiveInfinity;
        }

        // A brand-new image should appear fitted immediately, not glide in from the old view.
        control.ResetToFit(animate: false);
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Clip the (transformed) image to the viewport so panned/zoomed pixels never
        // spill over the neighbouring panels.
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight) };
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

    /// <summary>Scale to <paramref name="newScale"/> while keeping <paramref name="anchor"/> fixed on screen.</summary>
    private void ZoomAbout(Point anchor, double newScale)
    {
        if (newScale <= 0)
        {
            return;
        }

        // The element is centred, so the composition maps a content point p to
        // (layoutOffset + scale*p + translate). Anchor the zoom about the cursor by solving for the
        // new translate that holds the content point currently under the cursor in place.
        double factor = newScale / _scale;
        double offsetX = (Viewport.ActualWidth - DisplayImage.ActualWidth) / 2.0;
        double offsetY = (Viewport.ActualHeight - DisplayImage.ActualHeight) / 2.0;
        _translateX = (anchor.X - offsetX) - (factor * (anchor.X - offsetX - _translateX));
        _translateY = (anchor.Y - offsetY) - (factor * (anchor.Y - offsetY - _translateY));
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
        _offsetSpring.FinalValue = new Vector3((float)_translateX, (float)_translateY, ShadowDepth);
        _imageVisual.StartAnimation("Scale", _scaleSpring);
        _imageVisual.StartAnimation("Translation", _offsetSpring);
    }

    /// <summary>Write the current target scale/offset immediately (used for panning and new-image fit).</summary>
    private void ApplyTransformInstant()
    {
        ClampTranslation();

        // A direct property set stops any in-flight animation on that property, then holds.
        _imageVisual.Scale = new Vector3((float)_scale, (float)_scale, 1f);
        _imageVisual.Properties.InsertVector3("Translation", new Vector3((float)_translateX, (float)_translateY, ShadowDepth));
    }

    // Keep the (scaled) image an exact fit to the viewport: it can be panned only as far as its own
    // edges — no over-pan slack, no gap. The centred element wraps its content tightly (no letterbox),
    // so clamping against the element's own bounds is symmetric regardless of aspect.
    private void ClampTranslation()
    {
        _translateX = ClampAxis(_translateX, Viewport.ActualWidth, DisplayImage.ActualWidth, _scale);
        _translateY = ClampAxis(_translateY, Viewport.ActualHeight, DisplayImage.ActualHeight, _scale);
    }

    private static double ClampAxis(double translate, double viewport, double element, double scale)
    {
        if (viewport <= 0 || element <= 0)
        {
            return translate;
        }

        // Content is laid out centred (offset) and the visual scales from the element's top-left,
        // so on-screen it spans [offset + translate, offset + translate + content].
        double offset = (viewport - element) / 2.0;
        double content = element * scale;
        double lower = viewport - offset - content;
        double upper = -offset;

        // Content smaller than the viewport can't be panned — hold it centred. Otherwise pan only
        // until an edge meets the matching viewport edge (a flush, exact fit — no slack).
        return lower > upper
            ? ((viewport - content) / 2.0) - offset
            : Math.Clamp(translate, lower, upper);
    }
}
