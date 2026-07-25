namespace Snaply.App.Tests;

public sealed class RenderedImageTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Constructor_copies_the_encoded_image()
    {
        byte[] source = OnePixelPng.ToArray();

        var image = new RenderedImage(source, 1, 1);
        source[0] = 0;

        Assert.Equal(137, image.Png.Span[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA==")]
    [InlineData("bm90IGEgcG5nIGhlYWRlciBhdCBhbGw=")]
    public void Constructor_rejects_invalid_png_header(string base64)
    {
        byte[] bytes = string.IsNullOrEmpty(base64)
            ? []
            : Convert.FromBase64String(base64);

        Assert.Throws<ArgumentException>(() => new RenderedImage(bytes, 1, 1));
    }

    [Fact]
    public void Constructor_rejects_mismatched_dimensions()
    {
        Assert.Throws<ArgumentException>(() => new RenderedImage(OnePixelPng, 2, 1));
        Assert.Throws<ArgumentException>(() => new RenderedImage(OnePixelPng, 1, 2));
    }
}
