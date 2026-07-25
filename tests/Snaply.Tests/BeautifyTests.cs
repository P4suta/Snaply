using Snaply.Imaging;

namespace Snaply.Tests;

public sealed class BeautifyTests
{
    [Theory]
    [InlineData(1, 0, 0, 0, 0, 255)]
    [InlineData(0.5f, 0, 0, 0, 0, 188)]
    [InlineData(0.001f, 0, 0, 0, 0, 3)]
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
    public void ScRgb_conversion_preserves_premultiplied_alpha()
    {
        Half[] rgba =
        [
            (Half)0.5f, (Half)0, (Half)0, (Half)0.5f,
            (Half)1, (Half)1, (Half)1, (Half)0,
        ];
        var bgra = new byte[8];

        bool toneMapped = ScRgbToneMapper.ConvertToBgra8(
            rgba,
            bgra,
            TestContext.Current.CancellationToken);

        Assert.False(toneMapped);
        Assert.Equal([0, 0, 128, 128, 0, 0, 0, 0], bgra);
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

        Assert.Equal([0, 255, 0, 255, 0, 0, 0, 0], bgra);
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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ScRgbToneMapper.ConvertToBgra8(rgba, bgra, cancellation.Token));
    }

    [Theory]
    [InlineData(1920, 1080, 86, 19, 11, 8)]
    [InlineData(3840, 2160, 160, 32, 16, 12)]
    [InlineData(512, 4096, 41, 9, 5, 4)]
    [InlineData(1, 1, 32, 8, 5, 3)]
    [InlineData(1000, 2000, 80, 18, 11, 7)]
    public void Layout_has_exact_proportional_geometry(
        int width,
        int height,
        int padding,
        int radius,
        int shadowBlur,
        int shadowOffset)
    {
        BeautifyLayoutResult layout = BeautifyLayout.Compute(new PixelSize(width, height));

        Assert.Equal(new PixelSize(width + (padding * 2), height + (padding * 2)), layout.Canvas);
        Assert.Equal(new PixelRect(padding, padding, width, height), layout.Image);
        Assert.Equal(radius, layout.CornerRadius);
        Assert.Equal(shadowBlur, layout.ShadowBlur);
        Assert.Equal(shadowOffset, layout.ShadowOffset);
        Assert.True((layout.ShadowBlur * 3) + layout.ShadowOffset <= padding);
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
    public void Palette_preserves_variation_without_crushing_multiple_channels()
    {
        Rgba[] colors =
        [
            new(180, 100, 100),
            new(170, 100, 80),
            new(100, 150, 170),
            new(120, 170, 100),
            new(255, 0, 0),
            new(0, 255, 0),
            new(0, 0, 255),
        ];

        ColorPalette[] palettes = colors
            .Select(color => ColorPalette.Create(color, 123, 456))
            .ToArray();
        Assert.Equal(palettes.Length, palettes.Distinct().Count());
        foreach (ColorPalette palette in palettes)
        {
            Assert.NotEqual(palette.Start, palette.End);
            Assert.True(CountBoundaryChannels(palette.Start) <= 1);
            Assert.True(CountBoundaryChannels(palette.End) <= 1);
        }
    }

    private static int CountBoundaryChannels(Rgba color) =>
        new[] { color.R, color.G, color.B }.Count(channel => channel is 0 or 255);
}
