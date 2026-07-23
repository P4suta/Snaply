namespace Snaply.Imaging;

internal readonly record struct BeautifyLayoutResult(
    PixelSize Canvas,
    PixelRect Image,
    float CornerRadius,
    float ShadowBlur,
    float ShadowOffset);

internal static class BeautifyLayout
{
    private const double PaddingRatio = 0.08;
    private const double RadiusRatio = 0.018;
    private const int MinimumPadding = 32;
    private const int MaximumPadding = 160;
    private const int MinimumRadius = 8;
    private const int MaximumRadius = 32;

    internal static BeautifyLayoutResult Compute(PixelSize source)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        int shorterSide = Math.Min(source.Width, source.Height);
        int padding = Math.Clamp(
            (int)Math.Round(shorterSide * PaddingRatio, MidpointRounding.AwayFromZero),
            MinimumPadding,
            MaximumPadding);
        int radius = Math.Clamp(
            (int)Math.Round(shorterSide * RadiusRatio, MidpointRounding.AwayFromZero),
            MinimumRadius,
            MaximumRadius);

        int canvasWidth = checked(source.Width + (padding * 2));
        int canvasHeight = checked(source.Height + (padding * 2));

        return new BeautifyLayoutResult(
            new PixelSize(canvasWidth, canvasHeight),
            new PixelRect(padding, padding, source.Width, source.Height),
            radius,
            Math.Max(16, radius * 2),
            Math.Max(8, radius));
    }
}
