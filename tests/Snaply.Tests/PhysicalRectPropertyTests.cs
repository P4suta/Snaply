using FsCheck.Xunit;
using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// Property-based tests pinning the algebraic laws of <see cref="PhysicalRect"/>'s set operations —
/// the composite-region math that <c>ResolveGroupRegion</c> relies on. Representative-value cases
/// live in <c>PhysicalRectTests</c>; these assert the laws hold across the whole (bounded) input
/// space. "Up to empty" accounts for the documented rule that an empty operand is ignored, so two
/// distinct empty rectangles are treated as the same (no) region.
/// </summary>
public class PhysicalRectPropertyTests
{
    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Union_IsCommutative_UpToEmpty(PhysicalRect a, PhysicalRect b) =>
        SameRegion(a.Union(b), b.Union(a));

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Union_IsAssociative_UpToEmpty(PhysicalRect a, PhysicalRect b, PhysicalRect c) =>
        SameRegion(a.Union(b).Union(c), a.Union(b.Union(c)));

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Union_IsIdempotent(PhysicalRect a) =>
        a.Union(a) == a;

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Union_EmptyIsIdentity(PhysicalRect a) =>
        SameRegion(a.Union(default), a) && default(PhysicalRect).Union(a) == a;

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Union_ContainsBothOperands(PhysicalRect a, PhysicalRect b)
    {
        PhysicalRect u = a.Union(b);
        return Covers(u, a) && Covers(u, b);
    }

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Union_IsEmpty_OnlyWhenBothOperandsEmpty(PhysicalRect a, PhysicalRect b) =>
        a.Union(b).IsEmpty == (a.IsEmpty && b.IsEmpty);

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Intersect_IsCommutative(PhysicalRect a, PhysicalRect b) =>
        a.Intersect(b) == b.Intersect(a);

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Intersect_IsIdempotent_UpToEmpty(PhysicalRect a) =>
        SameRegion(a.Intersect(a), a);

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Intersect_IsContainedInBothOperands(PhysicalRect a, PhysicalRect b)
    {
        PhysicalRect r = a.Intersect(b);
        return r.IsEmpty || (Covers(a, r) && Covers(b, r));
    }

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Bounds_IsOrderIndependent_UpToEmpty(PhysicalRect a, PhysicalRect b, PhysicalRect c)
    {
        PhysicalRect forward = PhysicalRect.Bounds(new[] { a, b, c });
        PhysicalRect reverse = PhysicalRect.Bounds(new[] { c, b, a });
        return SameRegion(forward, reverse);
    }

    [Property(Arbitrary = new[] { typeof(GeometryArbitraries) })]
    public bool Bounds_EqualsLeftFoldOfUnion(PhysicalRect a, PhysicalRect b, PhysicalRect c)
    {
        PhysicalRect bounds = PhysicalRect.Bounds(new[] { a, b, c });
        PhysicalRect fold = default(PhysicalRect).Union(a).Union(b).Union(c);
        return bounds == fold;
    }

    // Two rectangles describe the same region when they are equal, or both have no area at all.
    private static bool SameRegion(PhysicalRect p, PhysicalRect q) =>
        (p.IsEmpty && q.IsEmpty) || p == q;

    // outer fully covers inner (an empty inner is trivially covered — it has no area to contain).
    private static bool Covers(PhysicalRect outer, PhysicalRect inner) =>
        inner.IsEmpty
        || (outer.X <= inner.X && outer.Y <= inner.Y && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom);
}
