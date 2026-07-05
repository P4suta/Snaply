namespace Snaply.Core.Models;

/// <summary>
/// A straight (non-premultiplied) 8-bit RGBA colour. Core defines its own colour
/// type rather than pulling in Windows.UI.Color or System.Drawing so the domain
/// stays free of any platform reference; the renderer adapter maps this to its
/// native colour at the edge.
/// </summary>
/// <param name="R">Red channel, 0–255.</param>
/// <param name="G">Green channel, 0–255.</param>
/// <param name="B">Blue channel, 0–255.</param>
/// <param name="A">Alpha channel, 0 (transparent) – 255 (opaque).</param>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    /// <summary>An opaque colour from red, green and blue channels.</summary>
    /// <param name="r">Red channel, 0–255.</param>
    /// <param name="g">Green channel, 0–255.</param>
    /// <param name="b">Blue channel, 0–255.</param>
    /// <returns>The opaque colour (alpha 255).</returns>
    public static Rgba FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    /// <summary>Fully transparent black.</summary>
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    /// <summary>Opaque white.</summary>
    public static readonly Rgba White = new(255, 255, 255, 255);
}
