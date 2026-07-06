using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// Explicit, representative cases for <see cref="LogicalRect.ToPhysical"/> — the DPI conversion the
/// doc comment calls "the single value that decides whether a screenshot is crisp or blurry". The
/// tiling invariant is covered by <c>LogicalRectTilingTests</c>; here we pin exact pixel results at
/// common scales, negative (multi-monitor) offsets, the zero-DPI degenerate case, and the
/// away-from-zero midpoint rounding.
/// </summary>
public class LogicalRectTests
{
    [Theory]
    [InlineData(96, 100)] // 100%
    [InlineData(120, 125)] // 125%
    [InlineData(144, 150)] // 150%
    [InlineData(168, 175)] // 175%
    [InlineData(192, 200)] // 200%
    public void ToPhysical_ScalesOriginRectByDpi(double dpiValue, int expectedExtent)
    {
        var logical = new LogicalRect(0, 0, 100, 100);

        PhysicalRect physical = logical.ToPhysical(new Dpi(dpiValue));

        Assert.Equal(new PhysicalRect(0, 0, expectedExtent, expectedExtent), physical);
    }

    [Fact]
    public void ToPhysical_NegativeOffset_MapsIntoNegativeVirtualDesktopSpace()
    {
        // A monitor left of the primary sits at a negative virtual-desktop offset.
        var logical = new LogicalRect(-100, -50, 100, 100);

        PhysicalRect physical = logical.ToPhysical(new Dpi(144)); // 150%

        Assert.Equal(new PhysicalRect(-150, -75, 150, 150), physical);
    }

    [Fact]
    public void ToPhysical_ZeroDpi_CollapsesToEmpty()
    {
        PhysicalRect physical = new LogicalRect(10, 20, 300, 200).ToPhysical(new Dpi(0));

        Assert.Equal(default, physical);
        Assert.True(physical.IsEmpty);
    }

    [Fact]
    public void ToPhysical_RoundsHalvesAwayFromZero_Positive()
    {
        // At 100% scale, edges of 0.5 and 1.5 round to 1 and 2 (away from zero, not bankers').
        PhysicalRect physical = new LogicalRect(0.5, 0.5, 1.0, 1.0).ToPhysical(Dpi.Default);

        Assert.Equal(new PhysicalRect(1, 1, 1, 1), physical);
    }

    [Fact]
    public void ToPhysical_RoundsHalvesAwayFromZero_Negative()
    {
        // -0.5 rounds to -1 (away from zero); the right edge at +0.5 rounds to +1, so width is 2.
        PhysicalRect physical = new LogicalRect(-0.5, 0, 1.0, 1.0).ToPhysical(Dpi.Default);

        Assert.Equal(new PhysicalRect(-1, 0, 2, 1), physical);
    }
}
