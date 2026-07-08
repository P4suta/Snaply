using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// Boundary behaviour of <see cref="PhysicalRect.Intersect"/> — the strict-inequality edges and
/// the AND that together mean "only a positive-area overlap counts". Used to clamp a composite
/// region to the monitor that captures it, so a zero-area or single-axis touch must be empty, not
/// a degenerate rectangle.
/// </summary>
public class GeometryIntersectTests
{
    [Fact]
    public void Intersect_ProperOverlap_ReturnsTheSharedRectangle()
    {
        var a = new PhysicalRect(0, 0, 10, 10);
        var b = new PhysicalRect(5, 5, 10, 10);

        Assert.Equal(new PhysicalRect(5, 5, 5, 5), a.Intersect(b));
    }

    [Fact]
    public void Intersect_EdgesTouchingHorizontally_IsEmptyNotZeroWidth()
    {
        // a's right edge (x=10) meets b's left edge (x=10): right == left, so no positive overlap.
        var a = new PhysicalRect(0, 0, 10, 10);
        var b = new PhysicalRect(10, 0, 10, 10);

        Assert.Equal(default, a.Intersect(b));
    }

    [Fact]
    public void Intersect_EdgesTouchingVertically_IsEmptyNotZeroHeight()
    {
        // a's bottom edge (y=10) meets b's top edge (y=10): bottom == top, so no positive overlap.
        var a = new PhysicalRect(0, 0, 10, 10);
        var b = new PhysicalRect(0, 10, 10, 10);

        Assert.Equal(default, a.Intersect(b));
    }

    [Fact]
    public void Intersect_OverlapOnOneAxisOnly_IsEmpty()
    {
        // Overlaps in X but is disjoint in Y: the AND must reject it (an OR would build a
        // negative-height rectangle).
        var a = new PhysicalRect(0, 0, 10, 10);
        var b = new PhysicalRect(5, 20, 10, 10);

        Assert.Equal(default, a.Intersect(b));
    }

    [Fact]
    public void Intersect_FullyDisjoint_IsEmpty()
    {
        var a = new PhysicalRect(0, 0, 10, 10);
        var b = new PhysicalRect(100, 100, 10, 10);

        Assert.Equal(default, a.Intersect(b));
    }
}
