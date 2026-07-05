using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Core.Beautify;

/// <summary>Where the screenshot sits inside the beautified canvas, and the canvas size.</summary>
/// <param name="Canvas">The full beautified canvas size in physical pixels.</param>
/// <param name="Image">Where the screenshot is placed within the canvas.</param>
public readonly record struct BeautifyLayoutResult(PhysicalSize Canvas, PhysicalRect Image);

/// <summary>
/// The pure geometry of beautification: given a source size and a
/// <see cref="BeautifySpec"/>, it decides the final canvas size and where the
/// screenshot is placed — no pixels, no GPU, no platform. This is the deliberate
/// separation that keeps the visual decision unit-testable; the Win2D renderer is
/// a thin adapter that just draws what this returns.
/// </summary>
public static class BeautifyLayout
{
    /// <summary>Compute the canvas size and centred image rect for a source of <paramref name="source"/> pixels.</summary>
    /// <param name="source">The captured screenshot size in physical pixels.</param>
    /// <param name="spec">The beautify spec describing padding and aspect ratio.</param>
    /// <returns>The canvas size and the placed image rectangle.</returns>
    public static BeautifyLayoutResult Compute(PhysicalSize source, BeautifySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        Padding pad = spec.Padding;

        // Canvas before any aspect-ratio adjustment: source plus its padding.
        double canvasW = source.Width + pad.Horizontal;
        double canvasH = source.Height + pad.Vertical;

        // The image's position within that base canvas.
        double imageX = pad.Left;
        double imageY = pad.Top;

        // Grow (never shrink, never crop) the canvas to hit a target ratio by
        // adding symmetric slack on the axis that is too small, re-centring the image.
        if (spec.Aspect.Ratio() is { } targetRatio)
        {
            double currentRatio = canvasW / canvasH;
            if (currentRatio < targetRatio)
            {
                double targetW = canvasH * targetRatio;
                double extra = targetW - canvasW;
                imageX += extra / 2.0;
                canvasW = targetW;
            }
            else if (currentRatio > targetRatio)
            {
                double targetH = canvasW / targetRatio;
                double extra = targetH - canvasH;
                imageY += extra / 2.0;
                canvasH = targetH;
            }
        }

        var canvas = new PhysicalSize(
            (int)Math.Round(canvasW, MidpointRounding.AwayFromZero),
            (int)Math.Round(canvasH, MidpointRounding.AwayFromZero));

        var imageRect = new PhysicalRect(
            (int)Math.Round(imageX, MidpointRounding.AwayFromZero),
            (int)Math.Round(imageY, MidpointRounding.AwayFromZero),
            source.Width,
            source.Height);

        return new BeautifyLayoutResult(canvas, imageRect);
    }
}
