namespace Snaply.Core.Geometry;

/// <summary>
/// Screen DPI, where 96 DPI == 100% scale. This is the single value that decides
/// whether a screenshot is crisp or blurry: capture and crop must happen in
/// physical pixels, and this type is the bridge between the logical coordinates
/// the OS hands the UI and the physical pixels the capture pipeline works in.
/// </summary>
/// <param name="Value">The DPI value, where 96 == 100% scale.</param>
public readonly record struct Dpi(double Value)
{
    /// <summary>The Windows baseline: 96 DPI == 100% scaling.</summary>
    public static readonly Dpi Default = new(96.0);

    /// <summary>Multiplier over the 96-DPI baseline (1.5 at 150% scaling).</summary>
    public double Scale => Value / 96.0;
}

/// <summary>A size in physical device pixels.</summary>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
public readonly record struct PhysicalSize(int Width, int Height)
{
    /// <summary>Total pixel count (width times height).</summary>
    public int Area => Width * Height;
}

/// <summary>
/// A rectangle in physical device pixels — the unit the capture pipeline uses so
/// no fractional scaling ever stretches the bitmap.
/// </summary>
/// <param name="X">Left edge in physical pixels.</param>
/// <param name="Y">Top edge in physical pixels.</param>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
public readonly record struct PhysicalRect(int X, int Y, int Width, int Height)
{
    /// <summary>The rectangle's extent as a <see cref="PhysicalSize"/>.</summary>
    public PhysicalSize Size => new(Width, Height);
}

/// <summary>
/// A rectangle in logical (DIP) coordinates — what the overlay window and pointer
/// report. Converted to <see cref="PhysicalRect"/> before anything is captured.
/// </summary>
/// <param name="X">Left edge in logical (DIP) units.</param>
/// <param name="Y">Top edge in logical (DIP) units.</param>
/// <param name="Width">Width in logical (DIP) units.</param>
/// <param name="Height">Height in logical (DIP) units.</param>
public readonly record struct LogicalRect(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// Convert to physical pixels at the given DPI. Rounding is done on the edges
    /// (not width/height) so adjacent regions tile without gaps or overlap.
    /// </summary>
    /// <param name="dpi">The DPI to scale by.</param>
    /// <returns>The equivalent rectangle in physical pixels.</returns>
    public PhysicalRect ToPhysical(Dpi dpi)
    {
        double scale = dpi.Scale;
        int left = (int)Math.Round(X * scale, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(Y * scale, MidpointRounding.AwayFromZero);
        int right = (int)Math.Round((X + Width) * scale, MidpointRounding.AwayFromZero);
        int bottom = (int)Math.Round((Y + Height) * scale, MidpointRounding.AwayFromZero);
        return new PhysicalRect(left, top, right - left, bottom - top);
    }
}
