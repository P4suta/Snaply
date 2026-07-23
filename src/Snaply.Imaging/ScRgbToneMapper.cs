namespace Snaply.Imaging;

internal static class ScRgbToneMapper
{
    internal static bool ConvertToBgra8(
        ReadOnlySpan<Half> rgba,
        Span<byte> bgra,
        CancellationToken cancellationToken = default)
    {
        if (rgba.Length % 4 != 0 || bgra.Length != rgba.Length)
        {
            throw new ArgumentException("Pixel buffers have incompatible lengths.");
        }

        bool toneMap = RequiresToneMapping(rgba);
        for (int source = 0, destination = 0; source < rgba.Length; source += 4, destination += 4)
        {
            if ((source & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bgra[destination] = ToSrgbByte((float)rgba[source + 2], toneMap);
            bgra[destination + 1] = ToSrgbByte((float)rgba[source + 1], toneMap);
            bgra[destination + 2] = ToSrgbByte((float)rgba[source], toneMap);
            bgra[destination + 3] = ToAlphaByte((float)rgba[source + 3]);
        }

        return toneMap;
    }

    private static bool RequiresToneMapping(ReadOnlySpan<Half> rgba)
    {
        for (int index = 0; index < rgba.Length; index += 4)
        {
            if ((float)rgba[index] > 1
                || (float)rgba[index + 1] > 1
                || (float)rgba[index + 2] > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToSrgbByte(float linear, bool toneMap)
    {
        if (float.IsNaN(linear) || linear <= 0)
        {
            return 0;
        }

        if (float.IsPositiveInfinity(linear))
        {
            return byte.MaxValue;
        }

        float value = linear;
        if (toneMap)
        {
            value = Math.Clamp(
                (value * ((2.51f * value) + 0.03f))
                / ((value * ((2.43f * value) + 0.59f)) + 0.14f),
                0,
                1);
        }
        else
        {
            value = Math.Min(value, 1);
        }

        float srgb = value <= 0.0031308f
            ? 12.92f * value
            : (1.055f * MathF.Pow(value, 1 / 2.4f)) - 0.055f;
        return (byte)MathF.Round(srgb * byte.MaxValue, MidpointRounding.AwayFromZero);
    }

    private static byte ToAlphaByte(float alpha)
    {
        float value = float.IsNaN(alpha) ? 0 : Math.Clamp(alpha, 0, 1);
        return (byte)MathF.Round(value * byte.MaxValue, MidpointRounding.AwayFromZero);
    }
}
