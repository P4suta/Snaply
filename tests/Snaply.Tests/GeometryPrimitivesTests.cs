using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// The small value types under <see cref="Snaply.Core.Geometry"/> — <see cref="Dpi"/>,
/// <see cref="PhysicalSize"/>, and the derived members of <see cref="PhysicalRect"/> — that the
/// capture math is built on. Cheap to test, and their boundaries (zero DPI, integer-overflow area,
/// the half-open empty definition) are easy to regress.
/// </summary>
public class GeometryPrimitivesTests
{
    [Fact]
    public void Dpi_Default_Is96AtUnitScale()
    {
        Assert.Equal(96.0, Dpi.Default.Value);
        Assert.Equal(1.0, Dpi.Default.Scale);
    }

    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(192, 2.0)]
    [InlineData(0, 0.0)]
    public void Dpi_Scale_IsValueOver96(double value, double expectedScale)
    {
        Assert.Equal(expectedScale, new Dpi(value).Scale);
    }

    [Fact]
    public void PhysicalSize_Area_IsWidthTimesHeight()
    {
        Assert.Equal(2_073_600, new PhysicalSize(1920, 1080).Area);
    }

    [Fact]
    public void PhysicalSize_Area_OverflowsToNegativeForHugeSizes()
    {
        // Area is a 32-bit product; a 2.5-billion-pixel size overflows int and wraps negative.
        // Documents the hazard so a future switch to a wider/checked type is a conscious change.
        Assert.True(new PhysicalSize(50_000, 50_000).Area < 0);
    }

    [Fact]
    public void PhysicalRect_Size_RoundTripsExtent()
    {
        Assert.Equal(new PhysicalSize(30, 40), new PhysicalRect(1, 2, 30, 40).Size);
    }

    [Fact]
    public void PhysicalRect_RightAndBottom_AreExclusiveEdges()
    {
        var rect = new PhysicalRect(10, 20, 30, 40);

        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
    }

    [Theory]
    [InlineData(0, 10, true)] // zero width
    [InlineData(10, 0, true)] // zero height
    [InlineData(-5, 10, true)] // negative width
    [InlineData(10, -5, true)] // negative height
    [InlineData(1, 1, false)] // smallest non-empty
    [InlineData(10, 10, false)]
    public void PhysicalRect_IsEmpty_WhenAnyExtentIsNonPositive(int width, int height, bool expected)
    {
        Assert.Equal(expected, new PhysicalRect(0, 0, width, height).IsEmpty);
    }
}
