namespace Snaply.Imaging;

internal readonly record struct Rgba(byte R, byte G, byte B, byte A = byte.MaxValue);

internal readonly record struct ColorPalette(Rgba Start, Rgba End, double AngleDegrees)
{
    internal static ColorPalette Create(Rgba average, ulong imageHash, uint salt)
    {
        (double lightness, double chroma, double hue) = RgbToOklch(average);
        ulong seed = MixSeed(imageHash, salt);
        double hueEntropy = Unit(seed, 8);
        double travelEntropy = Unit(seed, 26);
        double spanEntropy = Unit(seed, 44);
        double colourfulness = Math.Min(1, chroma / 0.13);
        double baseHue = chroma > 1e-4
            ? hue + ((hueEntropy - 0.5) * 180)
            : hueEntropy * 360;
        double travel = 30 + ((1 - colourfulness) * 20) + (travelEntropy * 20);
        double centre = 0.5 + ((0.5 - lightness) / 3);
        double half = (0.12 + (spanEntropy * 0.12)) / 2;
        double startLightness = Math.Clamp(centre + half, 0.42, 0.80);
        double endLightness = Math.Clamp(centre - half, 0.42, 0.80);
        double outputChroma = Math.Clamp(Math.Max(chroma * 1.2, 0.11), 0, 0.19);

        return new ColorPalette(
            OklchToRgba(startLightness, outputChroma, baseHue),
            OklchToRgba(endLightness, outputChroma, baseHue + travel),
            90 + (hueEntropy * 90));
    }

    private static (double Lightness, double Chroma, double Hue) RgbToOklch(Rgba color)
    {
        double red = SrgbToLinear(color.R / 255d);
        double green = SrgbToLinear(color.G / 255d);
        double blue = SrgbToLinear(color.B / 255d);
        double l = Math.Cbrt((0.4122214708 * red) + (0.5363325363 * green) + (0.0514459929 * blue));
        double m = Math.Cbrt((0.2119034982 * red) + (0.6806995451 * green) + (0.1073969566 * blue));
        double s = Math.Cbrt((0.0883024619 * red) + (0.2817188376 * green) + (0.6299787005 * blue));
        double lightness = (0.2104542553 * l) + (0.793617785 * m) - (0.0040720468 * s);
        double a = (1.9779984951 * l) - (2.428592205 * m) + (0.4505937099 * s);
        double b = (0.0259040371 * l) + (0.7827717662 * m) - (0.808675766 * s);
        double degrees = Math.Atan2(b, a) * 180 / Math.PI;
        return (lightness, Math.Sqrt((a * a) + (b * b)), (degrees + 360) % 360);
    }

    private static Rgba OklchToRgba(double lightness, double chroma, double hueDegrees)
    {
        double hue = hueDegrees * Math.PI / 180;
        double a = chroma * Math.Cos(hue);
        double b = chroma * Math.Sin(hue);
        double l = Math.Pow(lightness + (0.3963377774 * a) + (0.2158037573 * b), 3);
        double m = Math.Pow(lightness - (0.1055613458 * a) - (0.0638541728 * b), 3);
        double s = Math.Pow(lightness - (0.0894841775 * a) - (1.291485548 * b), 3);

        return new Rgba(
            ToByte((4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s)),
            ToByte((-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s)),
            ToByte((-0.0041960863 * l) - (0.7034186147 * m) + (1.707614701 * s)));
    }

    private static ulong MixSeed(ulong hash, uint salt)
    {
        ulong value = hash ^ ((ulong)salt * 0x9E3779B97F4A7C15UL);
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCDUL;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53UL;
        value ^= value >> 33;
        return value;
    }

    private static double Unit(ulong seed, int shift) =>
        ((seed >> shift) & 0xFFFFF) / 1048576d;

    private static double SrgbToLinear(double value) =>
        value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static byte ToByte(double linear)
    {
        double clamped = Math.Clamp(linear, 0, 1);
        double srgb = clamped <= 0.0031308
            ? 12.92 * clamped
            : (1.055 * Math.Pow(clamped, 1 / 2.4)) - 0.055;
        return (byte)Math.Round(srgb * 255, MidpointRounding.AwayFromZero);
    }
}
