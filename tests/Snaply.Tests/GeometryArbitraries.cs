using FsCheck;
using FsCheck.Fluent;
using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// FsCheck arbitraries for the geometry types, bounded so coordinate arithmetic stays well clear
/// of <see cref="int"/> overflow (overflow itself is pinned by explicit unit tests, not sampled
/// here). Referenced from property tests via <c>[Property(Arbitrary = new[] { typeof(...) })]</c>.
/// </summary>
internal static class GeometryArbitraries
{
    /// <summary>Rectangles with coordinates in [-10000, 10000] and extents in [0, 10000].</summary>
    public static Arbitrary<PhysicalRect> PhysicalRects() =>
        Arb.From(
            from x in Coord()
            from y in Coord()
            from w in Extent()
            from h in Extent()
            select new PhysicalRect(x, y, w, h));

    /// <summary>Logical rectangles with sub-pixel (thousandths) coordinates and extents.</summary>
    public static Arbitrary<LogicalRect> LogicalRects() =>
        Arb.From(
            from x in LogicalCoord()
            from y in LogicalCoord()
            from w in LogicalExtent()
            from h in LogicalExtent()
            select new LogicalRect(x, y, w, h));

    /// <summary>DPI values across a realistic 50%–400% scaling range, with fractional steps.</summary>
    public static Arbitrary<Dpi> Dpis() =>
        Arb.From(from hundredths in Gen.Choose(4800, 38400) select new Dpi(hundredths / 100.0));

    private static Gen<int> Coord() => Gen.Choose(-10000, 10000);

    private static Gen<int> Extent() => Gen.Choose(0, 10000);

    private static Gen<double> LogicalCoord() => from i in Gen.Choose(-1000000, 1000000) select i / 1000.0;

    private static Gen<double> LogicalExtent() => from i in Gen.Choose(0, 2000000) select i / 1000.0;
}
