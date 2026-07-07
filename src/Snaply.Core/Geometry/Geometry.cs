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

    /// <summary>True when the rectangle has no area (a zero or negative extent).</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The right edge (exclusive), i.e. <see cref="X"/> + <see cref="Width"/>.</summary>
    public int Right => X + Width;

    /// <summary>The bottom edge (exclusive), i.e. <see cref="Y"/> + <see cref="Height"/>.</summary>
    public int Bottom => Y + Height;

    /// <summary>
    /// The smallest rectangle containing both this and <paramref name="other"/>. An empty
    /// operand is ignored (returns the other), so unioning over a mix of real and empty
    /// windows never drags the result to the origin.
    /// </summary>
    /// <param name="other">The rectangle to union with.</param>
    /// <returns>The bounding rectangle of the two.</returns>
    public PhysicalRect Union(PhysicalRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        int left = Math.Min(X, other.X);
        int top = Math.Min(Y, other.Y);
        int right = Math.Max(Right, other.Right);
        int bottom = Math.Max(Bottom, other.Bottom);
        return new PhysicalRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The overlap of this and <paramref name="other"/>, or an empty rectangle when they do
    /// not intersect. Used to clamp a composite region to the monitor that will capture it.
    /// </summary>
    /// <param name="other">The rectangle to intersect with.</param>
    /// <returns>The overlapping rectangle, or empty when disjoint.</returns>
    public PhysicalRect Intersect(PhysicalRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top
            ? new PhysicalRect(left, top, right - left, bottom - top)
            : default;
    }

    /// <summary>True when the point (<paramref name="x"/>, <paramref name="y"/>) falls inside this rectangle.</summary>
    /// <param name="x">The point's X coordinate (physical px).</param>
    /// <param name="y">The point's Y coordinate (physical px).</param>
    /// <returns><c>true</c> when the point is within the rectangle.</returns>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>
    /// This rectangle re-expressed relative to <paramref name="origin"/>'s top-left
    /// (translated by -origin.X, -origin.Y). Turns a screen-space frame rect into an
    /// offset inside a window-local captured bitmap — e.g. the visible frame within a
    /// window-rect-sized PrintWindow bitmap.
    /// </summary>
    /// <param name="origin">The rectangle whose top-left becomes the new origin.</param>
    /// <returns>This rectangle shifted so <paramref name="origin"/>'s top-left is (0, 0).</returns>
    public PhysicalRect RelativeTo(PhysicalRect origin) =>
        this with { X = X - origin.X, Y = Y - origin.Y };

    /// <summary>
    /// The bounding rectangle of a sequence of rectangles (empty ones ignored), or an empty
    /// rectangle when the sequence yields nothing with area.
    /// </summary>
    /// <param name="rects">The rectangles to bound.</param>
    /// <returns>The smallest rectangle containing them all.</returns>
    public static PhysicalRect Bounds(IEnumerable<PhysicalRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);

        PhysicalRect result = default;
        foreach (PhysicalRect rect in rects)
        {
            result = result.Union(rect);
        }

        return result;
    }
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
