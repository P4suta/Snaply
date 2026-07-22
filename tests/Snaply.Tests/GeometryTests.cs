using FsCheck;
using FsCheck.Xunit;
using Snaply.Imaging;

namespace Snaply.Tests;

public sealed class GeometryTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, true)]
    [InlineData(-1, 1, true)]
    [InlineData(1, -1, true)]
    [InlineData(1, 1, false)]
    public void Size_empty_state_uses_each_dimension(int width, int height, bool expected)
    {
        var size = new PixelSize(width, height);

        Assert.Equal(expected, size.IsEmpty);
    }

    [Fact]
    public void Size_area_is_exact()
    {
        Assert.Equal(12L, new PixelSize(3, 4).Area);
        Assert.Equal(4_611_686_014_132_420_609L, new PixelSize(int.MaxValue, int.MaxValue).Area);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, true)]
    [InlineData(-1, 1, true)]
    [InlineData(1, -1, true)]
    [InlineData(1, 1, false)]
    public void Rectangle_empty_state_uses_each_dimension(int width, int height, bool expected)
    {
        var rectangle = new PixelRect(4, 5, width, height);

        Assert.Equal(expected, rectangle.IsEmpty);
    }

    [Fact]
    public void Rectangle_edges_and_size_are_exact()
    {
        var rectangle = new PixelRect(-10, -20, 30, 50);

        Assert.Equal(20, rectangle.Right);
        Assert.Equal(30, rectangle.Bottom);
        Assert.Equal(new PixelSize(30, 50), rectangle.Size);
    }

    [Theory]
    [InlineData(0, 0, 10, 10, 5, 4, 10, 10, 5, 4, 5, 6)]
    [InlineData(-20, -10, 10, 10, -15, -20, 20, 15, -15, -10, 5, 5)]
    [InlineData(0, 0, 20, 20, 5, 5, 2, 3, 5, 5, 2, 3)]
    public void Intersection_returns_exact_overlap(
        int x1,
        int y1,
        int width1,
        int height1,
        int x2,
        int y2,
        int width2,
        int height2,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var first = new PixelRect(x1, y1, width1, height1);
        var second = new PixelRect(x2, y2, width2, height2);

        Assert.Equal(
            new PixelRect(expectedX, expectedY, expectedWidth, expectedHeight),
            first.Intersect(second));
    }

    [Theory]
    [InlineData(0, 0, 10, 10, 10, 0, 5, 5)]
    [InlineData(0, 0, 10, 10, 0, 10, 5, 5)]
    [InlineData(0, 0, 10, 10, 20, 20, 5, 5)]
    public void Intersection_of_disjoint_or_touching_rectangles_is_empty(
        int x1,
        int y1,
        int width1,
        int height1,
        int x2,
        int y2,
        int width2,
        int height2)
    {
        var first = new PixelRect(x1, y1, width1, height1);
        var second = new PixelRect(x2, y2, width2, height2);

        Assert.Equal(default, first.Intersect(second));
    }

    [Property(MaxTest = 10_000)]
    public bool Intersection_is_commutative(
        int x1,
        int y1,
        PositiveInt width1,
        PositiveInt height1,
        int x2,
        int y2,
        PositiveInt width2,
        PositiveInt height2)
    {
        var first = SafeRect(x1, y1, width1.Get, height1.Get);
        var second = SafeRect(x2, y2, width2.Get, height2.Get);
        return first.Intersect(second) == second.Intersect(first);
    }

    [Property(MaxTest = 10_000)]
    public bool Union_contains_both_non_empty_operands(
        int x1,
        int y1,
        PositiveInt width1,
        PositiveInt height1,
        int x2,
        int y2,
        PositiveInt width2,
        PositiveInt height2)
    {
        var first = SafeRect(x1, y1, width1.Get, height1.Get);
        var second = SafeRect(x2, y2, width2.Get, height2.Get);

        try
        {
            PixelRect union = first.Union(second);
            bool contains = union.X <= first.X
                && union.Y <= first.Y
                && union.Right >= first.Right
                && union.Bottom >= first.Bottom
                && union.X <= second.X
                && union.Y <= second.Y
                && union.Right >= second.Right
                && union.Bottom >= second.Bottom;
            return contains;
        }
        catch (OverflowException)
        {
            return true;
        }
    }

    [Fact]
    public void Bounds_handles_negative_virtual_desktop_coordinates()
    {
        PixelRect bounds = PixelRect.Bounds(
        [
            new PixelRect(-3840, -400, 3840, 2160),
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(2560, 200, 1080, 1920),
        ]);

        Assert.Equal(new PixelRect(-3840, -400, 7480, 2520), bounds);
    }

    [Fact]
    public void Union_preserves_empty_operand_identity()
    {
        var rectangle = new PixelRect(-4, 7, 11, 13);

        Assert.Equal(rectangle, default(PixelRect).Union(rectangle));
        Assert.Equal(rectangle, rectangle.Union(default));
    }

    [Fact]
    public void Relative_coordinates_are_exact()
    {
        var rectangle = new PixelRect(-120, 250, 300, 400);
        var origin = new PixelRect(-500, -30, 1, 1);

        Assert.Equal(new PixelRect(380, 280, 300, 400), rectangle.RelativeTo(origin));
    }

    [Fact]
    public void Relative_coordinates_reject_overflow()
    {
        var xOverflow = new PixelRect(int.MaxValue, 0, 1, 1);
        var xOrigin = new PixelRect(int.MinValue, 0, 1, 1);
        var yOverflow = new PixelRect(0, int.MaxValue, 1, 1);
        var yOrigin = new PixelRect(0, int.MinValue, 1, 1);

        Assert.Throws<OverflowException>(() => xOverflow.RelativeTo(xOrigin));
        Assert.Throws<OverflowException>(() => yOverflow.RelativeTo(yOrigin));
    }

    [Fact]
    public void Bounds_of_empty_sequence_is_empty()
    {
        Assert.Equal(default, PixelRect.Bounds([]));
    }

    [Fact]
    public void Bounds_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => PixelRect.Bounds(null!));
    }

    [Fact]
    public void Dip_conversion_rounds_edges_so_tiles_remain_seamless()
    {
        PixelRect left = new DipRect(0, 0, 100.5, 40).ToPixels(1.25);
        PixelRect right = new DipRect(100.5, 0, 99.5, 40).ToPixels(1.25);

        Assert.Equal(left.Right, right.X);
        Assert.Equal(250, right.Right);
    }

    [Fact]
    public void Dip_conversion_scales_negative_coordinates_and_both_axes()
    {
        PixelRect pixels = new DipRect(-10.5, 20.5, 3.25, 4.75).ToPixels(2);

        Assert.Equal(new PixelRect(-21, 41, 6, 10), pixels);
    }

    [Fact]
    public void Dip_conversion_rejects_coordinate_overflow()
    {
        Assert.Throws<OverflowException>(() =>
            new DipRect(int.MaxValue, int.MinValue, 1, 1).ToPixels(2));
    }

    [Fact]
    public void Dip_conversion_rejects_dimension_overflow()
    {
        Assert.Throws<OverflowException>(() =>
            new DipRect(-1_000_000_000, 0, 2_000_000_000, 1).ToPixels(2));
        Assert.Throws<OverflowException>(() =>
            new DipRect(0, -1_000_000_000, 1, 2_000_000_000).ToPixels(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Dip_conversion_rejects_invalid_scale(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DipRect(0, 0, 1, 1).ToPixels(scale));
    }

    [Fact]
    public void Overflow_is_never_silently_wrapped()
    {
        var horizontalExtreme = new PixelRect(int.MaxValue, 0, 1, 1);
        var horizontalOther = new PixelRect(int.MinValue, 0, 1, 1);
        var verticalExtreme = new PixelRect(0, int.MaxValue, 1, 1);
        var verticalOther = new PixelRect(0, int.MinValue, 1, 1);

        Assert.Throws<OverflowException>(() => horizontalExtreme.Union(horizontalOther));
        Assert.Throws<OverflowException>(() => verticalExtreme.Union(verticalOther));
    }

    private static PixelRect SafeRect(int x, int y, int width, int height)
    {
        int safeX = Math.Clamp(x, -1_000_000, 1_000_000);
        int safeY = Math.Clamp(y, -1_000_000, 1_000_000);
        int safeWidth = Math.Clamp(width, 1, 100_000);
        int safeHeight = Math.Clamp(height, 1, 100_000);
        return new PixelRect(safeX, safeY, safeWidth, safeHeight);
    }
}
