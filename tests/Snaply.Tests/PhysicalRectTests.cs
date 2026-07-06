using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// Pure geometry for composite (window-with-popups) capture: unioning a window's bounds with its
/// dialogs, and clamping the result to a monitor. No display needed — the same platform-independence
/// that lets the capture math be unit-tested.
/// </summary>
public class PhysicalRectTests
{
    [Fact]
    public void Union_TwoOverlappingRects_ReturnsBoundingRect()
    {
        var a = new PhysicalRect(0, 0, 100, 100);
        var b = new PhysicalRect(50, 50, 100, 100);

        PhysicalRect union = a.Union(b);

        Assert.Equal(new PhysicalRect(0, 0, 150, 150), union);
    }

    [Fact]
    public void Union_IgnoresEmptyOperand()
    {
        var real = new PhysicalRect(10, 20, 30, 40);

        Assert.Equal(real, real.Union(default));
        Assert.Equal(real, default(PhysicalRect).Union(real));
    }

    [Fact]
    public void Bounds_OverManyRects_IgnoresEmptyAndBoundsTheRest()
    {
        PhysicalRect[] rects =
        [
            new(100, 100, 200, 150),   // an app window
            default,                    // a minimized/degenerate popup
            new(250, 200, 400, 300),   // a file picker overlapping and spilling right/down
        ];

        PhysicalRect bounds = PhysicalRect.Bounds(rects);

        Assert.Equal(new PhysicalRect(100, 100, 550, 400), bounds);
    }

    [Fact]
    public void Bounds_AllEmpty_IsEmpty()
    {
        PhysicalRect bounds = PhysicalRect.Bounds([default, default]);

        Assert.True(bounds.IsEmpty);
    }

    [Fact]
    public void Intersect_ClampsRegionToMonitor()
    {
        var monitor = new PhysicalRect(0, 0, 1920, 1080);
        var spilling = new PhysicalRect(1800, 1000, 400, 300); // runs off the bottom-right

        PhysicalRect clamped = spilling.Intersect(monitor);

        Assert.Equal(new PhysicalRect(1800, 1000, 120, 80), clamped);
    }

    [Fact]
    public void Intersect_Disjoint_IsEmpty()
    {
        var monitor = new PhysicalRect(0, 0, 100, 100);
        var elsewhere = new PhysicalRect(500, 500, 50, 50);

        Assert.True(elsewhere.Intersect(monitor).IsEmpty);
    }

    [Theory]
    [InlineData(50, 50, true)]
    [InlineData(0, 0, true)]
    [InlineData(100, 50, false)] // right edge is exclusive
    [InlineData(-1, 50, false)]
    public void Contains_ChecksHalfOpenBounds(int x, int y, bool expected)
    {
        var rect = new PhysicalRect(0, 0, 100, 100);

        Assert.Equal(expected, rect.Contains(x, y));
    }

    [Fact]
    public void Contains_BottomAndRightEdges_AreExclusive()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);

        Assert.True(rect.Contains(99, 99)); // last inside pixel
        Assert.False(rect.Contains(100, 99)); // on the right edge (exclusive)
        Assert.False(rect.Contains(99, 100)); // on the bottom edge (exclusive)
    }

    [Fact]
    public void Intersect_TouchingEdges_IsEmpty()
    {
        // Rectangles that share only an edge (right == left) overlap in zero area — the intersect
        // uses a strict inequality, so a 1px overlap intersects but a 0px touch does not.
        var left = new PhysicalRect(0, 0, 100, 100);
        var right = new PhysicalRect(100, 0, 100, 100);

        Assert.True(left.Intersect(right).IsEmpty);
    }

    [Fact]
    public void Union_DisjointRects_SpansTheGapBetweenThem()
    {
        var a = new PhysicalRect(0, 0, 10, 10);
        var b = new PhysicalRect(100, 100, 10, 10);

        Assert.Equal(new PhysicalRect(0, 0, 110, 110), a.Union(b));
    }

    [Fact]
    public void Bounds_SingleRect_ReturnsThatRect()
    {
        var only = new PhysicalRect(30, 40, 200, 150);

        Assert.Equal(only, PhysicalRect.Bounds([only]));
    }

    [Fact]
    public void Bounds_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PhysicalRect.Bounds(null!));
    }
}
