using System.Buffers.Binary;

namespace Snaply;

internal sealed class RenderedImage
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly byte[] _png;

    internal RenderedImage(ReadOnlySpan<byte> png, int width, int height)
    {
        Validate(png, width, height);
        _png = png.ToArray();
        Width = width;
        Height = height;
    }

    private RenderedImage(byte[] png, int width, int height)
    {
        Validate(png, width, height);
        _png = png;
        Width = width;
        Height = height;
    }

    internal static RenderedImage FromOwnedPng(byte[] png, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        return new RenderedImage(png, width, height);
    }

    internal ReadOnlyMemory<byte> Png => _png;

    internal int Width { get; }

    internal int Height { get; }

    private static void Validate(ReadOnlySpan<byte> png, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (png.Length < 24
            || !png[..PngSignature.Length].SequenceEqual(PngSignature)
            || BinaryPrimitives.ReadUInt32BigEndian(png[8..12]) != 13
            || !png[12..16].SequenceEqual("IHDR"u8))
        {
            throw new ArgumentException("PNG data must contain a valid IHDR header.", nameof(png));
        }

        int encodedWidth = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[16..20]));
        int encodedHeight = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[20..24]));
        if (encodedWidth != width || encodedHeight != height)
        {
            throw new ArgumentException(
                "PNG header dimensions must match the rendered image dimensions.",
                nameof(png));
        }
    }
}
