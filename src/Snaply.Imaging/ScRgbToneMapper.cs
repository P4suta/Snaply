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

        bool toneMap = RequiresToneMapping(rgba, cancellationToken);
        for (int source = 0, destination = 0; source < rgba.Length; source += 4, destination += 4)
        {
            if ((source & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            float alpha = NormalizeAlpha((float)rgba[source + 3]);
            bgra[destination] = ToPremultipliedSrgbByte(
                (float)rgba[source + 2],
                alpha,
                toneMap);
            bgra[destination + 1] = ToPremultipliedSrgbByte(
                (float)rgba[source + 1],
                alpha,
                toneMap);
            bgra[destination + 2] = ToPremultipliedSrgbByte(
                (float)rgba[source],
                alpha,
                toneMap);
            bgra[destination + 3] = ToAlphaByte(alpha);
        }

        return toneMap;
    }

    private static bool RequiresToneMapping(
        ReadOnlySpan<Half> rgba,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < rgba.Length; index += 4)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            float alpha = NormalizeAlpha((float)rgba[index + 3]);
            if (alpha > 0
                && ((float)rgba[index] / alpha > 1
                    || (float)rgba[index + 1] / alpha > 1
                    || (float)rgba[index + 2] / alpha > 1))
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToPremultipliedSrgbByte(
        float premultipliedLinear,
        float alpha,
        bool toneMap)
    {
        if (alpha <= 0 || float.IsNaN(premultipliedLinear) || premultipliedLinear <= 0)
        {
            return 0;
        }

        if (float.IsPositiveInfinity(premultipliedLinear))
        {
            return ToAlphaByte(alpha);
        }

        float value = premultipliedLinear / alpha;
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

        if (value >= 1)
        {
            return ToAlphaByte(alpha);
        }

        float srgb = value <= 0.0031308f
            ? 12.92f * value
            : (1.055f * MathF.Pow(value, 1 / 2.4f)) - 0.055f;
        return (byte)MathF.Round(
            srgb * alpha * byte.MaxValue,
            MidpointRounding.AwayFromZero);
    }

    private static float NormalizeAlpha(float alpha) =>
        float.IsNaN(alpha) ? 0 : Math.Clamp(alpha, 0, 1);

    private static byte ToAlphaByte(float alpha) =>
        (byte)MathF.Round(alpha * byte.MaxValue, MidpointRounding.AwayFromZero);
}
