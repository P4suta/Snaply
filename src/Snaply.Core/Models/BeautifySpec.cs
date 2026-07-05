namespace Snaply.Core.Models;

/// <summary>
/// The full description of how a raw screenshot is turned into a "beautified"
/// image (the Xnapper / CleanShot look): a background, breathing-room padding,
/// rounded corners and a drop shadow. It is an immutable value — every UI tweak
/// produces a new spec, and the renderer is a pure function of (source, spec).
/// All measurements are in physical pixels, matching the captured bitmap.
/// </summary>
public sealed record BeautifySpec
{
    /// <summary>The canvas backdrop behind the screenshot.</summary>
    public Background Background { get; init; } = Background.DefaultGradient;

    /// <summary>Breathing-room padding between the screenshot and the canvas edge.</summary>
    public Padding Padding { get; init; } = Padding.Uniform(64);

    /// <summary>
    /// When true (the default), the pipeline auto-derives <see cref="Padding"/> from the capture
    /// size and ignores the literal above; set false to honour an explicit <see cref="Padding"/>
    /// (e.g. the CLI's <c>--padding</c> / the MCP <c>padding</c> argument).
    /// </summary>
    public bool AutoPadding { get; init; } = true;

    /// <summary>Corner radius of the screenshot, in physical pixels. 0 == square.</summary>
    public double CornerRadius { get; init; } = 16;

    /// <summary>
    /// When true (the default), the pipeline auto-derives <see cref="CornerRadius"/> from the
    /// capture size; set false to honour an explicit <see cref="CornerRadius"/>.
    /// </summary>
    public bool AutoCornerRadius { get; init; } = true;

    /// <summary>The drop shadow cast by the screenshot.</summary>
    public ShadowSpec Shadow { get; init; } = ShadowSpec.Default;

    /// <summary>Target aspect ratio of the final canvas.</summary>
    public AspectPreset Aspect { get; init; } = AspectPreset.Auto;

    /// <summary>The out-of-the-box spec: gradient background, uniform padding, soft shadow.</summary>
    public static BeautifySpec Default { get; } = new();
}

/// <summary>The canvas backdrop behind the screenshot.</summary>
public abstract record Background
{
    private Background()
    {
    }

    /// <summary>A single flat colour.</summary>
    /// <param name="Color">The fill colour.</param>
    public sealed record Solid(Rgba Color) : Background;

    /// <summary>Linear gradient from <paramref name="Start"/> to <paramref name="End"/> at the given angle.</summary>
    /// <param name="Start">The colour at the start of the gradient.</param>
    /// <param name="End">The colour at the end of the gradient.</param>
    /// <param name="AngleDegrees">The gradient direction, in degrees.</param>
    public sealed record LinearGradient(Rgba Start, Rgba End, double AngleDegrees) : Background;

    /// <summary>A background image or wallpaper loaded from disk by the renderer.</summary>
    /// <param name="Path">Filesystem path to the image.</param>
    public sealed record ImageFile(string Path) : Background;

    /// <summary>
    /// Marker: derive a harmonious background from the captured image. The pipeline resolves this
    /// to a concrete <see cref="LinearGradient"/> (via <c>BeautifyDefaults.SuggestBackground</c>)
    /// before rendering, so the renderer never sees it.
    /// </summary>
    public sealed record Auto : Background;

    /// <summary>A calm blue-violet diagonal gradient — the default "looks good out of the box".</summary>
    public static Background DefaultGradient { get; } =
        new LinearGradient(new Rgba(99, 102, 241, 255), new Rgba(168, 85, 247, 255), 135);
}

/// <summary>Per-edge padding in physical pixels between the screenshot and the canvas edge.</summary>
/// <param name="Left">Left padding in physical pixels.</param>
/// <param name="Top">Top padding in physical pixels.</param>
/// <param name="Right">Right padding in physical pixels.</param>
/// <param name="Bottom">Bottom padding in physical pixels.</param>
public readonly record struct Padding(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Padding equal on all four edges.</summary>
    /// <param name="all">The padding, in physical pixels, applied to every edge.</param>
    /// <returns>A uniform <see cref="Padding"/>.</returns>
    public static Padding Uniform(double all) => new(all, all, all, all);

    /// <summary>Combined left + right padding.</summary>
    public double Horizontal => Left + Right;

    /// <summary>Combined top + bottom padding.</summary>
    public double Vertical => Top + Bottom;
}

/// <summary>A drop shadow cast by the screenshot onto the background.</summary>
/// <param name="OffsetX">Horizontal offset in physical pixels.</param>
/// <param name="OffsetY">Vertical offset in physical pixels.</param>
/// <param name="BlurRadius">Blur radius in physical pixels.</param>
/// <param name="Opacity">Shadow opacity, 0–1.</param>
/// <param name="Color">Shadow colour.</param>
public readonly record struct ShadowSpec(double OffsetX, double OffsetY, double BlurRadius, double Opacity, Rgba Color)
{
    /// <summary>A soft, subtly-dropped default shadow.</summary>
    public static ShadowSpec Default { get; } = new(0, 24, 48, 0.35, new Rgba(0, 0, 0, 255));

    /// <summary>No shadow at all.</summary>
    public static ShadowSpec None { get; } = new(0, 0, 0, 0, Rgba.Transparent);
}

/// <summary>
/// Target aspect ratio of the final canvas. Anything other than <see cref="Auto"/>
/// grows the padding symmetrically so the screenshot stays centred — never crops.
/// </summary>
public enum AspectPreset
{
    /// <summary>Canvas is exactly source + padding; no ratio enforced.</summary>
    Auto,

    /// <summary>1:1.</summary>
    Square,

    /// <summary>4:3.</summary>
    Standard,

    /// <summary>16:9.</summary>
    Wide,
}

/// <summary>Helpers for <see cref="AspectPreset"/>.</summary>
public static class AspectPresetExtensions
{
    /// <summary>Width/height ratio, or <c>null</c> for <see cref="AspectPreset.Auto"/>.</summary>
    /// <param name="preset">The preset to resolve.</param>
    /// <returns>The width/height ratio, or <c>null</c> when no ratio is enforced.</returns>
    public static double? Ratio(this AspectPreset preset) => preset switch
    {
        AspectPreset.Square => 1.0,
        AspectPreset.Standard => 4.0 / 3.0,
        AspectPreset.Wide => 16.0 / 9.0,
        _ => null,
    };
}
