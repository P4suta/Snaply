using FsCheck.Xunit;
using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// The tiling invariant of <see cref="LogicalRect.ToPhysical"/>: because the conversion rounds the
/// <em>edges</em> (left/top/right/bottom) rather than width/height, two logically adjacent
/// rectangles must convert to physically adjacent ones — the right edge of one landing exactly on
/// the left edge of the next, with no gap and no overlap, at any DPI. This is the property the doc
/// comment promises and the reason the capture never seams; had it rounded width/height instead,
/// these would drift by a pixel. Sampled across the whole bounded input space by FsCheck.
/// </summary>
public class LogicalRectTilingTests
{
    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool ToPhysical_AdjacentColumnsTileWithoutGapOrOverlap(LogicalRect a, Dpi dpi)
    {
        LogicalRect right = a with { X = a.X + a.Width };
        return a.ToPhysical(dpi).Right == right.ToPhysical(dpi).X;
    }

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool ToPhysical_AdjacentRowsTileWithoutGapOrOverlap(LogicalRect a, Dpi dpi)
    {
        LogicalRect below = a with { Y = a.Y + a.Height };
        return a.ToPhysical(dpi).Bottom == below.ToPhysical(dpi).Y;
    }
}
