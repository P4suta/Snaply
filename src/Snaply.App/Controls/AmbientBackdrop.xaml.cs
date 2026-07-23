using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Visual = Microsoft.UI.Composition.Visual;

namespace Snaply.Controls;

/// <summary>
/// A soft ambient backdrop for the empty canvas, generated entirely from a small aesthetic model
/// rather than hand-tuned constants — every value is derived, moment to moment, from the frame time:
/// <list type="bullet">
/// <item>Colour lives in <b>OKLCH</b> (perceptually uniform): a constant, gentle chroma at a high
/// lightness means the "softness" is identical across every hue (unlike HSL, which looks harsher at
/// some hues). Perceptual uniformity is the beauty.</item>
/// <item>Hues are spread by the <b>golden angle</b> (137.5°) — the most even distribution on the
/// wheel — and the whole set rotates slowly, so all hues pass by without restriction.</item>
/// <item>Motion is a quasiperiodic <b>Lissajous</b> drift whose per-axis frequencies are scaled by
/// powers of the <b>golden ratio</b>: their ratios are irrational, so the field never repeats and
/// never settles into a visible loop.</item>
/// </list>
/// Deterministic (the maths is the design, not randomness) yet perpetually fresh. Purely decorative:
/// no pointer input, and it only runs while <see cref="IsActive"/> — the per-frame updates stop and
/// the control hides once a capture covers it.
/// </summary>
internal sealed partial class AmbientBackdrop : UserControl
{
    /// <summary>Identifies the <see cref="IsActive"/> dependency property.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(AmbientBackdrop),
        new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>Identifies the <see cref="IsMuted"/> dependency property.</summary>
    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted),
        typeof(bool),
        typeof(AmbientBackdrop),
        new PropertyMetadata(false));

    private const int BlobCount = 5; // a Fibonacci count; plays nicely with the golden-angle spread

    private const double Phi = 1.6180339887498949;

    // OKLCH soft pastel band: high lightness + gentle, constant chroma = uniform softness per hue.
    private const double SoftLightness = 0.82;
    private const double SoftChroma = 0.11;

    // Layering opacities — soft enough that the overlapping fields blend into one gentle wash.
    private const byte BlobCentreAlpha = 150;
    private const byte BaseWashAlpha = 150;

    // Timing: the base hue completes a full turn every HuePeriod; blobs drift on a base period, each
    // axis detuned by a golden-ratio power so no two frequencies are commensurable.
    private const double HuePeriodSeconds = 90.0;      // slow, but clearly perceptible
    private const double DriftBasePeriodSeconds = 24.0;
    private const double BreatheDepth = 0.08;          // ±8% scale
    private const double AmplitudeFraction = 0.16;     // drift amplitude vs the shorter card side

    // "Energy" scales saturation, presence and the hue-drift rate together. It eases between 1 (empty
    // canvas — full, vivid) and MutedEnergy (an image is shown — calm, so it doesn't fight the image),
    // over EnergyTau so the change is a gentle fade, never an instant switch.
    private const double MutedEnergy = 0.4;
    private const double EnergyTau = 1.8; // seconds (time constant of the ease)

    private const double GoldenAngleDeg = 360.0 / (Phi * Phi); // ≈137.5077°
    private const double GoldenAngleRad = GoldenAngleDeg * Math.PI / 180.0;
    private const double Omega0 = 2.0 * Math.PI / DriftBasePeriodSeconds;
    private const double HueDegPerSecond = 360.0 / HuePeriodSeconds;

    private readonly Ellipse[] _blobs = new Ellipse[BlobCount];
    private readonly RadialGradientBrush[] _brushes = new RadialGradientBrush[BlobCount];
    private readonly Visual[] _visuals = new Visual[BlobCount];

    // Per-blob motion constants (depend only on the index, so computed once).
    private readonly double[] _freqX = new double[BlobCount];
    private readonly double[] _freqY = new double[BlobCount];
    private readonly double[] _freqScale = new double[BlobCount];
    private readonly double[] _phaseX = new double[BlobCount];
    private readonly double[] _phaseY = new double[BlobCount];
    private readonly double[] _phaseScale = new double[BlobCount];

    private double _amplitude;
    private double _energy = 1.0;   // eases toward MutedEnergy / 1 (see OnRendering)
    private double _hue;            // accumulated base hue (integrated, since its rate varies)
    private double _lastTime = -1;  // previous frame time; <0 means "no previous frame yet"
    private bool _loaded;
    private bool _running;

    /// <summary>Creates the control, builds its blobs and derives their per-index motion constants.</summary>
    public AmbientBackdrop()
    {
        InitializeComponent();

        for (int i = 0; i < BlobCount; i++)
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(0, 0, 0, 0) });
            brush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0, 0, 0, 0) });

            var blob = new Ellipse
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Fill = brush,
            };

            Root.Children.Add(blob);
            _blobs[i] = blob;
            _brushes[i] = brush;

            Visual visual = ElementCompositionPreview.GetElementVisual(blob);
            ElementCompositionPreview.SetIsTranslationEnabled(blob, true);
            _visuals[i] = visual;

            // Detune each axis by a golden-ratio power (centred on the base), so every frequency ratio
            // is irrational — the Lissajous figure never closes and the motion never repeats.
            double exponent = (i - ((BlobCount - 1) / 2.0)) * 0.4;
            _freqX[i] = Omega0 * Math.Pow(Phi, exponent);
            _freqY[i] = Omega0 * Math.Pow(Phi, exponent + 0.3);
            _freqScale[i] = Omega0 * Math.Pow(Phi, exponent - 0.5);
            _phaseX[i] = i * GoldenAngleRad;
            _phaseY[i] = (i * GoldenAngleRad) + (Math.PI / 2.0);
            _phaseScale[i] = (i * GoldenAngleRad) + (Math.PI / 4.0);
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Root.SizeChanged += OnRootSizeChanged;
    }

    /// <summary>
    /// Whether the backdrop is shown and animating. Bind this to the empty-canvas condition
    /// (e.g. <c>HasNoImage</c>): the control hides and its per-frame updates stop when it is <c>false</c>.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>
    /// When <c>true</c> the field calms down — lower saturation, fainter, and a slower colour drift —
    /// so it sits quietly behind a shown image. Bind to the "an image is present" condition
    /// (e.g. <c>HasImage</c>). The transition eases in/out; it is never an instant switch.
    /// </summary>
    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AmbientBackdrop)d).UpdateActivation();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        UpdateActivation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        StopRendering();
    }

    // Lay the blobs out to cover the card (phyllotaxis placement + generous size), clip to bounds,
    // and update the drift amplitude for the new size.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = Root.ActualWidth;
        double h = Root.ActualHeight;
        Root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };
        if (w <= 0 || h <= 0)
        {
            return;
        }

        double diagonal = Math.Sqrt((w * w) + (h * h));
        double spread = Math.Min(w, h) * 0.32;
        _amplitude = Math.Min(w, h) * AmplitudeFraction;

        for (int i = 0; i < BlobCount; i++)
        {
            // Big soft discs, sized off the diagonal so they overlap and fill the whole card.
            double size = diagonal * (0.7 + (0.12 * i));

            // Sunflower (golden-angle) placement so the centres are evenly, organically distributed.
            double radius = spread * Math.Sqrt((i + 0.5) / BlobCount);
            double angle = i * GoldenAngleRad;
            double cx = (w / 2.0) + (radius * Math.Cos(angle));
            double cy = (h / 2.0) + (radius * Math.Sin(angle));

            _blobs[i].Width = size;
            _blobs[i].Height = size;
            _blobs[i].Margin = new Thickness(cx - (size / 2.0), cy - (size / 2.0), 0, 0);
            _visuals[i].CenterPoint = new Vector3((float)(size / 2.0), (float)(size / 2.0), 0f);
        }
    }

    private void UpdateActivation()
    {
        Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (_loaded && IsActive)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _lastTime = -1; // so dt doesn't jump across a pause when we resume
        CompositionTarget.Rendering -= OnRendering;
    }

    // The whole look, derived fresh each frame from the current time.
    private void OnRendering(object? sender, object e)
    {
        double t = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
        double dt = _lastTime < 0 ? 0.0 : Math.Clamp(t - _lastTime, 0.0, 0.1);
        _lastTime = t;

        // Ease "energy" toward its target (frame-rate independent), then let it scale saturation,
        // presence and the hue-drift rate — so muting is a slow, coherent calming, not a hard cut.
        double target = IsMuted ? MutedEnergy : 1.0;
        _energy += (target - _energy) * (1.0 - Math.Exp(-dt / EnergyTau));

        // Integrate the base hue (its rate is energy-scaled, so it can't be a function of absolute t).
        _hue = (_hue + (HueDegPerSecond * _energy * dt)) % 360.0;

        double chroma = SoftChroma * _energy;
        byte blobAlpha = (byte)(BlobCentreAlpha * _energy);
        byte washAlpha = (byte)(BaseWashAlpha * _energy);

        for (int i = 0; i < BlobCount; i++)
        {
            // Colour: this blob's hue is the rotating base plus a golden-angle offset (even spread).
            (byte r, byte g, byte b) = OklchToRgb(SoftLightness, chroma, _hue + (i * GoldenAngleDeg));
            _brushes[i].GradientStops[0].Color = Color.FromArgb(blobAlpha, r, g, b);
            _brushes[i].GradientStops[1].Color = Color.FromArgb(0, r, g, b);

            // Motion: quasiperiodic Lissajous drift + a gentle breathing scale about the centre.
            float dx = (float)(_amplitude * Math.Sin((_freqX[i] * t) + _phaseX[i]));
            float dy = (float)(_amplitude * Math.Sin((_freqY[i] * t) + _phaseY[i]));
            _visuals[i].Properties.InsertVector3("Translation", new Vector3(dx, dy, 0f));

            float scale = (float)(1.0 + (BreatheDepth * Math.Sin((_freqScale[i] * t) + _phaseScale[i])));
            _visuals[i].Scale = new Vector3(scale, scale, 1f);
        }

        // Base wash: a soft two-tone that fills the whole card, its stops a golden-angle apart and
        // riding the same rotating hue, so there's always colour even between the blobs.
        (byte w0R, byte w0G, byte w0B) = OklchToRgb(SoftLightness, chroma * 0.85, _hue);
        (byte w1R, byte w1G, byte w1B) = OklchToRgb(SoftLightness, chroma * 0.85, _hue + GoldenAngleDeg);
        BaseWashStop0.Color = Color.FromArgb(washAlpha, w0R, w0G, w0B);
        BaseWashStop1.Color = Color.FromArgb(washAlpha, w1R, w1G, w1B);
    }

    // Perceptually-uniform OKLCH → opaque sRGB (gamut-clamped per channel). Kept self-contained here
    // so the backdrop depends on nothing beyond the framework; the maths mirrors the colour science
    // the app uses elsewhere (equal OKLCH steps look equally spaced, which is where the softness reads).
    private static (byte R, byte G, byte B) OklchToRgb(double lightness, double chroma, double hueDegrees)
    {
        double h = hueDegrees * Math.PI / 180.0;
        double a = chroma * Math.Cos(h);
        double bComponent = chroma * Math.Sin(h);

        double lRoot = lightness + (0.3963377774 * a) + (0.2158037573 * bComponent);
        double mRoot = lightness - (0.1055613458 * a) - (0.0638541728 * bComponent);
        double sRoot = lightness - (0.0894841775 * a) - (1.2914855480 * bComponent);
        double lCubed = lRoot * lRoot * lRoot;
        double mCubed = mRoot * mRoot * mRoot;
        double sCubed = sRoot * sRoot * sRoot;

        double red = (4.0767416621 * lCubed) - (3.3077115913 * mCubed) + (0.2309699292 * sCubed);
        double green = (-1.2684380046 * lCubed) + (2.6097574011 * mCubed) - (0.3413193965 * sCubed);
        double blue = (-0.0041960863 * lCubed) - (0.7034186147 * mCubed) + (1.7076147010 * sCubed);

        return (ToByte(LinearToSrgb(red)), ToByte(LinearToSrgb(green)), ToByte(LinearToSrgb(blue)));
    }

    private static double LinearToSrgb(double channel)
    {
        double c = Math.Clamp(channel, 0.0, 1.0);
        return c <= 0.0031308 ? (12.92 * c) : ((1.055 * Math.Pow(c, 1.0 / 2.4)) - 0.055);
    }

    private static byte ToByte(double channel) => (byte)Math.Clamp(Math.Round(channel * 255.0), 0, 255);
}
