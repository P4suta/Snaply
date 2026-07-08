using Snaply.Core.Beautify;
using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// Characterization ("golden") and exact-value tests for <see cref="BeautifyDefaults"/>.
///
/// The module's contract is "pure and deterministic — the same capture always yields the same
/// look", so its many tuned constants (padding/corner fractions and clamps, the gradient
/// derivation, the OKLCH colour matrices) are pinned to exact expected outputs here. Loose
/// invariant tests let those constants drift silently; these lock them down.
///
/// Cross-platform note: the seed/hash/entropy chain (and therefore the gradient angle) is pure
/// integer arithmetic, so angles are asserted exactly. Colour channels flow through OKLCH
/// (Cbrt/Pow/Cos/Sin) whose last bit can differ by a platform ULP, so RGB is asserted within ±1.
/// </summary>
public class BeautifyDefaultsGoldenTests
{
    private const int ColourTolerance = 1;

    // Padding: fraction (0.06 of the shorter side), rounded, clamped to [40, 180].
    // 400x300 -> 300*0.06=18 clamped up to the 40 floor; 700x700 -> 42 sits inside the band
    // (proves it isn't pinned to the floor); 2688x1728 -> 1728*0.06=103.68 -> 104 proves ShortSide
    // uses Min not Max; 3000 -> exactly 180; 4000 -> 240 clamped down to the 180 ceiling.
    [Theory]
    [InlineData(400, 300, 40)]
    [InlineData(700, 700, 42)]
    [InlineData(1000, 1000, 60)]
    [InlineData(2688, 1728, 104)]
    [InlineData(3000, 3000, 180)]
    [InlineData(4000, 4000, 180)]
    public void SuggestPadding_ScalesAndClampsToExactPixels(int width, int height, double expected)
    {
        Padding p = BeautifyDefaults.SuggestPadding(new PhysicalSize(width, height));

        Assert.Equal(expected, p.Left);
        Assert.Equal(expected, p.Top);
        Assert.Equal(expected, p.Right);
        Assert.Equal(expected, p.Bottom);
    }

    // Corner radius: fraction (0.018 of the shorter side), rounded, clamped to [10, 44].
    // 400x300 -> 5.4 clamped up to 10; 700x700 -> 12.6 -> 13 inside the band; 2500 -> 45 clamped
    // down to the 44 ceiling.
    [Theory]
    [InlineData(400, 300, 10)]
    [InlineData(700, 700, 13)]
    [InlineData(1000, 1000, 18)]
    [InlineData(2688, 1728, 31)]
    [InlineData(2500, 2500, 44)]
    [InlineData(4000, 4000, 44)]
    public void SuggestCornerRadius_ScalesAndClampsToExactPixels(int width, int height, double expected) =>
        Assert.Equal(expected, BeautifyDefaults.SuggestCornerRadius(new PhysicalSize(width, height)));

    // OklchToRgba: exact sRGB for known OKLCH inputs (pins the colour-science matrices).
    // c=0 is neutral grey at three lightnesses; the rest sweep chroma and hue.
    [Theory]
    [InlineData(0.0, 0.0, 0, 0, 0, 0)]
    [InlineData(1.0, 0.0, 0, 255, 255, 255)]
    [InlineData(0.5, 0.0, 0, 99, 99, 99)]
    [InlineData(0.8, 0.1, 210, 102, 207, 225)]
    [InlineData(0.6, 0.15, 30, 202, 87, 71)]
    [InlineData(0.7, 0.12, 120, 149, 169, 78)]
    [InlineData(0.5, 0.2, 300, 119, 58, 193)]
    [InlineData(0.85, 0.08, 90, 226, 204, 145)]
    [InlineData(0.4, 0.08, 90, 88, 69, 3)]
    public void OklchToRgba_MatchesKnownConversions(double l, double c, double h, int r, int g, int b)
    {
        Rgba actual = BeautifyDefaults.OklchToRgba(l, c, h);

        AssertRgb(r, g, b, actual);
        Assert.Equal(255, actual.A); // always fully opaque
    }

    // SuggestBackground: exact generated gradient for chromatic captures. Chromatic images sit
    // well clear of the neutral-hue branch (imageChroma > 1e-4), so their derivation path is
    // stable; angle is integer-derived (asserted exactly), RGB within ±1.
    [Theory]
    [InlineData(200, 100, 50, 0u, 110, 134, 0, 0, 112, 65, 172.5171947479248)]
    [InlineData(200, 100, 50, 1u, 174, 81, 178, 150, 11, 74, 95.43608665466309)]
    [InlineData(200, 100, 50, 7u, 207, 72, 70, 144, 45, 0, 124.89978790283203)]
    [InlineData(40, 40, 220, 0u, 220, 96, 192, 192, 36, 80, 168.9144515991211)]
    [InlineData(120, 200, 60, 0u, 0, 153, 179, 0, 77, 170, 170.20285606384277)]
    public void SuggestBackground_GeneratesExactGradient(
        int r,
        int g,
        int b,
        uint salt,
        int startR,
        int startG,
        int startB,
        int endR,
        int endG,
        int endB,
        double angle)
    {
        var gradient = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(r, g, b), salt);

        AssertRgb(startR, startG, startB, gradient.Start);
        AssertRgb(endR, endG, endB, gradient.End);
        Assert.Equal(255, gradient.Start.A);
        Assert.Equal(255, gradient.End.A);
        Assert.True(
            Math.Abs(angle - gradient.AngleDegrees) < 1e-3,
            $"angle {gradient.AngleDegrees} != expected {angle}");
    }

    [Fact]
    public void SuggestBackground_AngleAlwaysDiagonal()
    {
        // angle = 90 + uHue*90, uHue in [0,1)  ->  always in [90, 180).
        CapturedImage image = SolidImage(200, 100, 50);
        for (uint salt = 0; salt < 40; salt++)
        {
            var g = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(image, salt);
            Assert.InRange(g.AngleDegrees, 90.0, 180.0 - 1e-9);
        }
    }

    [Fact]
    public void SuggestBackground_AlwaysTwoOpaqueTones()
    {
        foreach ((int r, int g, int b) in new[] { (200, 100, 50), (40, 40, 220), (240, 240, 240), (0, 0, 0), (255, 255, 255) })
        {
            for (uint salt = 0; salt < 8; salt++)
            {
                var grad = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(r, g, b), salt);
                Assert.NotEqual(grad.Start, grad.End); // never a flat fill
                Assert.Equal(255, grad.Start.A);
                Assert.Equal(255, grad.End.A);
            }
        }
    }

    [Fact]
    public void SuggestBackground_NeutralImageStillGetsAColouredGradient()
    {
        // A perfectly grey capture takes the "no dominant hue -> fully random hue" branch; it must
        // still yield a saturated two-tone gradient, deterministically.
        var a = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(240, 240, 240));
        var b = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(240, 240, 240));

        Assert.Equal(a, b); // deterministic on the same platform
        Assert.NotEqual(a.Start, a.End); // still two tones
        Assert.True(a.Start.R != a.Start.G || a.Start.G != a.Start.B); // not a grey
    }

    [Fact]
    public void SuggestBackground_ThrowsOnNullImage() =>
        Assert.Throws<ArgumentNullException>(() => BeautifyDefaults.SuggestBackground(null!));

    private static void AssertRgb(int expectedR, int expectedG, int expectedB, Rgba actual)
    {
        Assert.True(
            Math.Abs(expectedR - actual.R) <= ColourTolerance &&
            Math.Abs(expectedG - actual.G) <= ColourTolerance &&
            Math.Abs(expectedB - actual.B) <= ColourTolerance,
            $"expected ~({expectedR},{expectedG},{expectedB}) but got ({actual.R},{actual.G},{actual.B})");
    }

    private static CapturedImage SolidImage(int r, int g, int b)
    {
        var size = new PhysicalSize(8, 8);
        byte[] bgra = new byte[size.Width * size.Height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)b;
            bgra[i + 1] = (byte)g;
            bgra[i + 2] = (byte)r;
            bgra[i + 3] = 255;
        }

        return new CapturedImage(size, bgra, Dpi.Default);
    }
}
