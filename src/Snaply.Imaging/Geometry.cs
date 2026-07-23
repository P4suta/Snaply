namespace Snaply.Imaging;

internal readonly record struct PixelSize(int Width, int Height)
{
    internal bool IsEmpty => Width <= 0 || Height <= 0;

    internal long Area => (long)Width * Height;
}

internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    internal bool IsEmpty => Width <= 0 || Height <= 0;

    internal long Right => (long)X + Width;

    internal long Bottom => (long)Y + Height;

    internal PixelSize Size => new(Width, Height);

    internal PixelRect Intersect(PixelRect other)
    {
        long left = Math.Max(X, other.X);
        long top = Math.Max(Y, other.Y);
        long right = Math.Min(Right, other.Right);
        long bottom = Math.Min(Bottom, other.Bottom);

        return right > left && bottom > top
            ? CreateChecked(left, top, right - left, bottom - top)
            : default;
    }

    internal PixelRect Union(PixelRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        long left = Math.Min(X, other.X);
        long top = Math.Min(Y, other.Y);
        long right = Math.Max(Right, other.Right);
        long bottom = Math.Max(Bottom, other.Bottom);
        return CreateChecked(left, top, right - left, bottom - top);
    }

    internal PixelRect RelativeTo(PixelRect origin) =>
        CreateChecked((long)X - origin.X, (long)Y - origin.Y, Width, Height);

    internal static PixelRect Bounds(IEnumerable<PixelRect> rectangles)
    {
        ArgumentNullException.ThrowIfNull(rectangles);

        PixelRect result = default;
        foreach (PixelRect rectangle in rectangles)
        {
            result = result.Union(rectangle);
        }

        return result;
    }

    private static PixelRect CreateChecked(long x, long y, long width, long height) =>
        new(checked((int)x), checked((int)y), checked((int)width), checked((int)height));
}

internal readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    internal PixelRect ToPixels(double scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, 0);

        int left = RoundChecked(X * scale);
        int top = RoundChecked(Y * scale);
        int right = RoundChecked((X + Width) * scale);
        int bottom = RoundChecked((Y + Height) * scale);
        return new PixelRect(left, top, checked(right - left), checked(bottom - top));
    }

    private static int RoundChecked(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
}
