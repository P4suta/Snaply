using Snaply.Core.Beautify;
using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Tests;

public class BeautifyDefaultsTests
{
    [Fact]
    public void Padding_ScalesWithShorterSide()
    {
        // 1728 shorter side * 0.06 = 103.68 -> 104, within the clamp range.
        Assert.Equal(104, BeautifyDefaults.SuggestPadding(new PhysicalSize(2688, 1728)).Left);
    }

    [Fact]
    public void Padding_ClampsSmallImagesUpAndHugeImagesDown()
    {
        Assert.Equal(40, BeautifyDefaults.SuggestPadding(new PhysicalSize(400, 300)).Left);   // min
        Assert.Equal(180, BeautifyDefaults.SuggestPadding(new PhysicalSize(4000, 4000)).Left); // max
    }

    [Fact]
    public void Padding_IsUniform()
    {
        Padding p = BeautifyDefaults.SuggestPadding(new PhysicalSize(2688, 1728));
        Assert.Equal(p.Left, p.Top);
        Assert.Equal(p.Left, p.Right);
        Assert.Equal(p.Left, p.Bottom);
    }

    [Fact]
    public void CornerRadius_ScalesAndClamps()
    {
        Assert.Equal(31, BeautifyDefaults.SuggestCornerRadius(new PhysicalSize(2688, 1728))); // 1728*0.018=31.1 -> 31
        Assert.Equal(10, BeautifyDefaults.SuggestCornerRadius(new PhysicalSize(400, 300)));   // min
        Assert.Equal(44, BeautifyDefaults.SuggestCornerRadius(new PhysicalSize(4000, 4000))); // max
    }

    [Fact]
    public void SuggestBackground_GeneratesATwoToneGradient()
    {
        var gradient = Assert.IsType<Background.LinearGradient>(BeautifyDefaults.SuggestBackground(SolidImage(200, 100, 50)));
        Assert.NotEqual(gradient.Start, gradient.End); // two generated tones, not a flat fill
    }

    [Fact]
    public void SuggestBackground_IsDeterministicForTheSameImage()
    {
        var a = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(200, 100, 50));
        var b = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(200, 100, 50));
        Assert.Equal(a.Start, b.Start);
        Assert.Equal(a.End, b.End);
        Assert.Equal(a.AngleDegrees, b.AngleDegrees);
    }

    [Fact]
    public void SuggestBackground_DiffersForDifferentlyColouredImages()
    {
        var warm = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(220, 40, 40)); // red
        var cool = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(40, 40, 220)); // blue
        Assert.NotEqual(warm.Start, cool.Start);
    }

    [Fact]
    public void SuggestBackground_StaysVividForNeutralImages()
    {
        // A plain grey UI has no dominant hue; the generator must still produce a coloured
        // gradient (seeded from the pixels), never a flat grey.
        var gradient = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(240, 240, 240));
        Assert.NotEqual(gradient.Start, gradient.End);
    }

    [Fact]
    public void SuggestBackground_VariesWithSalt()
    {
        CapturedImage image = SolidImage(200, 100, 50);
        Background a = BeautifyDefaults.SuggestBackground(image, salt: 1);
        Background b = BeautifyDefaults.SuggestBackground(image, salt: 2);
        Assert.NotEqual(a, b); // a little randomness so repeats aren't identical
    }

    [Fact]
    public void SuggestBackground_DegenerateImage_StillReturnsDeterministicGradient()
    {
        // A 0x0 / malformed buffer falls back to a neutral grey seed, but must still yield a
        // deterministic two-tone gradient rather than throwing or returning a flat fill.
        var empty = new CapturedImage(new PhysicalSize(0, 0), Array.Empty<byte>(), Dpi.Default);

        var a = Assert.IsType<Background.LinearGradient>(BeautifyDefaults.SuggestBackground(empty));
        var b = Assert.IsType<Background.LinearGradient>(BeautifyDefaults.SuggestBackground(empty));

        Assert.Equal(a, b);
        Assert.NotEqual(a.Start, a.End);
    }

    [Fact]
    public void SuggestBackground_SaltZero_IsTheDeterministicBaseline()
    {
        CapturedImage image = SolidImage(220, 40, 40);

        Assert.Equal(BeautifyDefaults.SuggestBackground(image, salt: 0), BeautifyDefaults.SuggestBackground(image, salt: 0));
    }

    [Fact]
    public void SuggestBackground_ProducesOpaqueStops()
    {
        var gradient = (Background.LinearGradient)BeautifyDefaults.SuggestBackground(SolidImage(120, 200, 60));

        Assert.Equal(255, gradient.Start.A);
        Assert.Equal(255, gradient.End.A);
    }

    private static CapturedImage SolidImage(byte r, byte g, byte b)
    {
        var size = new PhysicalSize(8, 8);
        byte[] bgra = new byte[size.Width * size.Height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }

        return new CapturedImage(size, bgra, Dpi.Default);
    }
}
