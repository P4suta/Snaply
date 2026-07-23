using Snaply.Imaging;

namespace Snaply.Tests;

public sealed class BeautifyTests
{
    [Theory]
    [InlineData(1, 0, 0, 0, 0, 255)]
    [InlineData(0.5f, 0, 0, 0, 0, 188)]
    [InlineData(0, 1, 0, 0, 255, 0)]
    [InlineData(0, 0, 1, 255, 0, 0)]
    public void Sdr_scRgb_conversion_preserves_values(
        float red,
        float green,
        float blue,
        byte expectedBlue,
        byte expectedGreen,
        byte expectedRed)
    {
        Half[] rgba = [(Half)red, (Half)green, (Half)blue, (Half)1];
        var bgra = new byte[4];

        bool toneMapped = ScRgbToneMapper.ConvertToBgra8(
            rgba,
            bgra,
            TestContext.Current.CancellationToken);

        Assert.False(toneMapped);
        Assert.Equal([expectedBlue, expectedGreen, expectedRed, 255], bgra);
    }

    [Theory]
    [InlineData(1.5f, 0, 0)]
    [InlineData(0, 1.5f, 0)]
    [InlineData(0, 0, 1.5f)]
    public void Hdr_detection_checks_every_color_channel(float red, float green, float blue)
    {
        Half[] rgba = [(Half)red, (Half)green, (Half)blue, (Half)1];
        var bgra = new byte[4];

        bool toneMapped = ScRgbToneMapper.ConvertToBgra8(
            rgba,
            bgra,
            TestContext.Current.CancellationToken);

        Assert.True(toneMapped);
    }

    [Fact]
    public void Hdr_scRgb_conversion_tone_maps_the_complete_frame()
    {
        Half[] rgba =
        [
            (Half)1, (Half)1, (Half)1, (Half)1,
            (Half)4, (Half)2, (Half)0.5f, (Half)1,
        ];
        var bgra = new byte[8];

        bool toneMapped = ScRgbToneMapper.ConvertToBgra8(
            rgba,
            bgra,
            TestContext.Current.CancellationToken);

        Assert.True(toneMapped);
        Assert.Equal([232, 232, 232, 255, 206, 245, 252, 255], bgra);
    }

    [Fact]
    public void ScRgb_conversion_handles_non_finite_values()
    {
        Half[] rgba =
        [
            Half.NaN,
            Half.PositiveInfinity,
            Half.NegativeInfinity,
            Half.PositiveInfinity,
            (Half)0.001f,
            (Half)0,
            (Half)0,
            Half.NaN,
        ];
        var bgra = new byte[8];

        _ = ScRgbToneMapper.ConvertToBgra8(
            rgba,
            bgra,
            TestContext.Current.CancellationToken);

        Assert.Equal([0, 255, 0, 255, 0, 0, 1, 0], bgra);
    }

    [Fact]
    public void ScRgb_conversion_rejects_incompatible_buffers()
    {
        ArgumentException malformed = Assert.Throws<ArgumentException>(
            () => ScRgbToneMapper.ConvertToBgra8(
                [(Half)0],
                new byte[1],
                TestContext.Current.CancellationToken));
        Assert.Equal("Pixel buffers have incompatible lengths.", malformed.Message);

        _ = Assert.Throws<ArgumentException>(
            () => ScRgbToneMapper.ConvertToBgra8(
                new Half[4],
                new byte[8],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ScRgb_conversion_honours_cancellation()
    {
        var rgba = new Half[4];
        var bgra = new byte[4];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

#pragma warning disable xUnit1051
        Assert.Throws<OperationCanceledException>(
            () => ScRgbToneMapper.ConvertToBgra8(rgba, bgra, cancellation.Token));
#pragma warning restore xUnit1051
    }

    [Theory]
    [InlineData(1920, 1080, 86, 19, 38)]
    [InlineData(3840, 2160, 160, 32, 64)]
    [InlineData(512, 4096, 41, 9, 18)]
    [InlineData(1, 1, 32, 8, 16)]
    [InlineData(1000, 2000, 80, 18, 36)]
    public void Layout_has_exact_proportional_geometry(
        int width,
        int height,
        int padding,
        int radius,
        int shadowBlur)
    {
        BeautifyLayoutResult layout = BeautifyLayout.Compute(new PixelSize(width, height));

        Assert.Equal(new PixelSize(width + (padding * 2), height + (padding * 2)), layout.Canvas);
        Assert.Equal(new PixelRect(padding, padding, width, height), layout.Image);
        Assert.Equal(radius, layout.CornerRadius);
        Assert.Equal(shadowBlur, layout.ShadowBlur);
        Assert.Equal(Math.Max(8, radius), layout.ShadowOffset);
    }

    [Fact]
    public void Layout_rejects_empty_source()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BeautifyLayout.Compute(default));
    }

    [Fact]
    public void Layout_rejects_overflow()
    {
        Assert.Throws<OverflowException>(() => BeautifyLayout.Compute(new PixelSize(int.MaxValue, 1)));
        Assert.Throws<OverflowException>(() => BeautifyLayout.Compute(new PixelSize(1, int.MaxValue)));
    }

    [Fact]
    public void Palette_is_deterministic_for_a_given_salt()
    {
        var input = new Rgba(123, 45, 210);

        Assert.Equal(
            ColorPalette.Create(input, 0x123456789ABCDEF0, 42),
            ColorPalette.Create(input, 0x123456789ABCDEF0, 42));
    }

    [Fact]
    public void Palette_varies_between_capture_salts()
    {
        var input = new Rgba(123, 45, 210);

        Assert.NotEqual(
            ColorPalette.Create(input, 0x123456789ABCDEF0, 1),
            ColorPalette.Create(input, 0x123456789ABCDEF0, 2));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    public void Palette_is_opaque(byte red, byte green, byte blue)
    {
        ColorPalette palette = ColorPalette.Create(new Rgba(red, green, blue), 123, 456);

        Assert.Equal(byte.MaxValue, palette.Start.A);
        Assert.Equal(byte.MaxValue, palette.End.A);
        Assert.NotEqual(palette.Start, palette.End);
        Assert.InRange(palette.AngleDegrees, 90, 180);
    }

    [Fact]
    public void Palette_matches_golden_values()
    {
        (Rgba Color, ulong Hash, uint Salt, ColorPalette Expected)[] cases =
        [
            (new Rgba(0, 0, 0), 0, 0,
                new ColorPalette(new Rgba(223, 137, 164), new Rgba(182, 109, 66), 90)),
            (new Rgba(255, 255, 255), ulong.MaxValue, uint.MaxValue,
                new ColorPalette(new Rgba(125, 46, 64), new Rgba(118, 60, 0), 92.35982894897461)),
            (new Rgba(123, 45, 210), 0x123456789ABCDEF0, 42,
                new ColorPalette(new Rgba(183, 65, 166), new Rgba(153, 0, 55), 152.32672691345215)),
            (new Rgba(1, 2, 3), 987654321, 123456789,
                new ColorPalette(new Rgba(200, 148, 215), new Rgba(165, 83, 75), 173.72474670410156)),
            (new Rgba(128, 128, 128), 123, 456,
                new ColorPalette(new Rgba(136, 87, 150), new Rgba(126, 47, 55), 169.57929611206055)),
            (new Rgba(255, 0, 0), 123, 456,
                new ColorPalette(new Rgba(136, 102, 0), new Rgba(0, 97, 0), 169.57929611206055)),
            (new Rgba(0, 255, 0), 123, 456,
                new ColorPalette(new Rgba(0, 105, 143), new Rgba(0, 73, 174), 169.57929611206055)),
            (new Rgba(0, 0, 255), 123, 456,
                new ColorPalette(new Rgba(185, 68, 170), new Rgba(163, 0, 56), 169.57929611206055)),
        ];

        foreach ((Rgba color, ulong hash, uint salt, ColorPalette expected) in cases)
        {
            ColorPalette actual = ColorPalette.Create(color, hash, salt);
            Assert.Equal(expected.Start, actual.Start);
            Assert.Equal(expected.End, actual.End);
            Assert.Equal(expected.AngleDegrees, actual.AngleDegrees, precision: 10);
        }
    }

    [Fact]
    public void Palette_preserves_mid_chroma_variation()
    {
        (Rgba Color, ColorPalette Expected)[] cases =
        [
            (new Rgba(180, 100, 100),
                new ColorPalette(new Rgba(138, 104, 0), new Rgba(49, 90, 0), 169.57929611206055)),
            (new Rgba(170, 100, 80),
                new ColorPalette(new Rgba(121, 114, 1), new Rgba(3, 93, 39), 169.57929611206055)),
            (new Rgba(100, 150, 170),
                new ColorPalette(new Rgba(109, 91, 161), new Rgba(116, 49, 92), 169.57929611206055)),
            (new Rgba(120, 170, 100),
                new ColorPalette(new Rgba(0, 120, 139), new Rgba(0, 79, 145), 169.57929611206055)),
        ];

        foreach ((Rgba color, ColorPalette expected) in cases)
        {
            ColorPalette actual = ColorPalette.Create(color, 123, 456);
            Assert.Equal(expected.Start, actual.Start);
            Assert.Equal(expected.End, actual.End);
            Assert.Equal(expected.AngleDegrees, actual.AngleDegrees, precision: 10);
        }
    }
}
