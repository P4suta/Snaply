using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Core.Beautify;

/// <summary>
/// Automatic beautify styling derived from the captured image: padding and corner radius scale
/// with the image size, and the background is a procedurally generated gradient (no fixed palette).
/// All pure and deterministic so the same capture always yields the same look.
/// </summary>
public static class BeautifyDefaults
{
    // Fractions of the shorter side, with clamps. Tuned for a Xnapper-like look;
    // adjust these four pairs to retaste the defaults globally.
    private const double PaddingFraction = 0.06;
    private const double PaddingMin = 40;
    private const double PaddingMax = 180;

    private const double CornerFraction = 0.018;
    private const double CornerMin = 10;
    private const double CornerMax = 44;

    /// <summary>Suggested uniform padding, in physical pixels, for a source of the given size.</summary>
    /// <param name="source">The captured image size.</param>
    /// <returns>A uniform padding scaled to the image and clamped to a tasteful range.</returns>
    public static Padding SuggestPadding(PhysicalSize source)
    {
        double pad = Math.Clamp(Math.Round(ShortSide(source) * PaddingFraction), PaddingMin, PaddingMax);
        return Padding.Uniform(pad);
    }

    /// <summary>Suggested screenshot corner radius, in physical pixels, for a source of the given size.</summary>
    /// <param name="source">The captured image size.</param>
    /// <returns>A corner radius scaled to the image and clamped to a tasteful range.</returns>
    public static double SuggestCornerRadius(PhysicalSize source) =>
        Math.Clamp(Math.Round(ShortSide(source) * CornerFraction), CornerMin, CornerMax);

    /// <summary>
    /// Procedurally generates a backdrop gradient for the captured image — no fixed palette and no
    /// fixed colour dials. The image is measured in OKLCH (perceptually uniform) and every gradient
    /// parameter is derived from that measurement plus a per-image pixel hash: base hue, the (wide)
    /// hue travel between the two stops, both stops' lightness (chosen to contrast the image) and
    /// chroma (vivid, honouring the image), and the angle. Rotating hue in OKLCH keeps it saturated
    /// and un-muddy. Deterministic per image; distinct images differ markedly. The only literals
    /// are colour-science (OKLab matrices), the wheel (360°) and the sRGB-gamut safety band — not
    /// tunable output values.
    /// </summary>
    /// <param name="image">The captured (raw) image to derive the gradient from.</param>
    /// <param name="salt">
    /// A per-capture salt mixed into the seed so repeat captures of the same thing aren't identical
    /// — a little life. <c>0</c> is the deterministic baseline (same image, same salt, same result).
    /// </param>
    /// <returns>A generated linear-gradient background.</returns>
    public static Background SuggestBackground(CapturedImage image, uint salt = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        (double red, double green, double blue, ulong hash) = SampleAverage(image);
        ulong seed = MixSeed(hash, salt);
        (double imageLightness, double imageChroma, double imageHue) = RgbToOklch(red, green, blue);

        // Entropy: three uniform [0,1) values from independent slices of the pixel hash.
        double uHue = Unit(seed, 8);
        double uTravel = Unit(seed, 26);
        double uSpan = Unit(seed, 44);

        // Colourfulness of the capture in [0,1], normalised against a strongly-saturated OKLCH
        // chroma — a scale for the measurement, from which the rest is derived.
        double colourfulness = Math.Min(1.0, imageChroma / 0.13);

        // Base hue: for a colourful image, orbit its dominant hue by a bold ±90° (so repeats swing
        // noticeably in colour while keeping a thread to the content); for a neutral one, go fully
        // random. The salt lives in uHue, so this is where most of the per-capture variety comes from.
        double baseHue = imageChroma > 1e-4
            ? imageHue + ((uHue - 0.5) * 180.0)
            : uHue * 360.0;

        // Hue travel between the two stops: a gentle, harmonious shift — analogous to a light
        // contrast, never as far as complementary. Neutral captures travel a little more; colourful
        // ones stay closer to their own hue.
        double travel = (360.0 / 12.0) + ((1.0 - colourfulness) * (360.0 / 18.0)) + (uTravel * (360.0 / 18.0)); // 30°..70°

        // Lightness sweeps around a centre that contrasts the image (dark shot -> lighter backdrop,
        // and vice-versa); the span (depth) also comes from the image. Held to the sRGB-safe band.
        double centre = 0.5 + ((0.5 - imageLightness) / 3.0);
        double half = (0.12 + (uSpan * 0.12)) / 2.0;
        double startLightness = Math.Clamp(centre + half, 0.42, 0.80);
        double endLightness = Math.Clamp(centre - half, 0.42, 0.80);

        // Chroma: honour a colourful image, lift a dull one so the backdrop is never muddy; capped
        // to stay in sRGB gamut across hues.
        double chroma = Math.Clamp(Math.Max(imageChroma * 1.2, 0.11), 0.0, 0.19);

        // A diagonal angle, also image-derived.
        double angle = 90.0 + (uHue * 90.0); // 90°..180°

        Rgba start = OklchToRgba(startLightness, chroma, baseHue);
        Rgba end = OklchToRgba(endLightness, chroma, baseHue + travel);
        return new Background.LinearGradient(start, end, angle);
    }

    private static int ShortSide(PhysicalSize source) => Math.Min(source.Width, source.Height);

    // Coarse sample grid (up to ~64x64 taps): the average colour (sRGB in [0,1]) plus an FNV-1a
    // hash of the sampled pixels as a stable per-image seed for the entropy.
    private static (double Red, double Green, double Blue, ulong Seed) SampleAverage(CapturedImage image)
    {
        ReadOnlySpan<byte> pixels = image.Bgra.Span;
        int width = image.Size.Width;
        int height = image.Size.Height;
        int stride = image.Stride;
        if (width <= 0 || height <= 0 || pixels.Length < (long)stride * height)
        {
            return (0.5, 0.5, 0.5, 1UL);
        }

        int stepX = Math.Max(1, width / 64);
        int stepY = Math.Max(1, height / 64);
        long sumR = 0, sumG = 0, sumB = 0;
        int count = 0;
        ulong hash = 1469598103934665603UL; // FNV-1a offset basis
        for (int y = 0; y < height; y += stepY)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                int i = row + (x * 4); // BGRA
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                sumB += b;
                sumG += g;
                sumR += r;
                count++;
                hash = (hash ^ r) * 1099511628211UL;
                hash = (hash ^ g) * 1099511628211UL;
                hash = (hash ^ b) * 1099511628211UL;
            }
        }

        if (count == 0)
        {
            return (0.5, 0.5, 0.5, 1UL);
        }

        return (sumR / (double)count / 255.0, sumG / (double)count / 255.0, sumB / (double)count / 255.0, hash);
    }

    // 20 well-distributed bits of the hash, at the given shift, mapped to a uniform [0,1).
    private static double Unit(ulong seed, int shift) => ((seed >> shift) & 0xFFFFF) / 1048576.0;

    // Fold the salt into the pixel hash and avalanche (murmur3 fmix64), so a different salt shifts
    // every derived value. salt 0 still fully mixes the hash (deterministic per image).
    private static ulong MixSeed(ulong hash, uint salt)
    {
        ulong x = hash ^ ((ulong)salt * 0x9E3779B97F4A7C15UL);
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return x;
    }

    // sRGB -> OKLab -> OKLCh (Björn Ottosson). Returns lightness, chroma and hue in degrees.
    private static (double Lightness, double Chroma, double HueDegrees) RgbToOklch(double r, double g, double b)
    {
        double lr = SrgbToLinear(r);
        double lg = SrgbToLinear(g);
        double lb = SrgbToLinear(b);

        double l = (0.4122214708 * lr) + (0.5363325363 * lg) + (0.0514459929 * lb);
        double m = (0.2119034982 * lr) + (0.6806995451 * lg) + (0.1073969566 * lb);
        double s = (0.0883024619 * lr) + (0.2817188376 * lg) + (0.6299787005 * lb);
        double lRoot = Math.Cbrt(l);
        double mRoot = Math.Cbrt(m);
        double sRoot = Math.Cbrt(s);

        double okL = (0.2104542553 * lRoot) + (0.7936177850 * mRoot) - (0.3271046800 * sRoot);
        double okA = (1.9779984951 * lRoot) - (2.4285922050 * mRoot) + (0.4505937099 * sRoot);
        double okB = (0.0259040371 * lRoot) + (0.7827717662 * mRoot) - (0.8086757660 * sRoot);

        double chroma = Math.Sqrt((okA * okA) + (okB * okB));
        double degrees = Math.Atan2(okB, okA) * 180.0 / Math.PI;
        double hue = (degrees + 360.0) % 360.0;
        return (okL, chroma, hue);
    }

    private static double SrgbToLinear(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    // OKLCH -> OKLab -> linear sRGB (Björn Ottosson's matrices) -> gamma-encoded sRGB bytes.
    private static Rgba OklchToRgba(double lightness, double chroma, double hueDegrees)
    {
        double h = hueDegrees * Math.PI / 180.0;
        double a = chroma * Math.Cos(h);
        double bComponent = chroma * Math.Sin(h);

        double lRoot = lightness + (0.3963377774 * a) + (0.2158037573 * bComponent);
        double mRoot = lightness - (0.1055613458 * a) - (0.0638541728 * bComponent);
        double sRoot = lightness - (0.0894841775 * a) - (1.2914855480 * bComponent);
        double lCubed = lRoot * lRoot * lRoot;
        double mCubed = mRoot * mRoot * mRoot;
        double sCubed = sRoot * sRoot * sRoot;

        double red = (4.0767416621 * lCubed) - (3.3077115913 * mCubed) + (0.2309699292 * sCubed);
        double green = (-1.2684380046 * lCubed) + (2.6097574011 * mCubed) - (0.3413193965 * sCubed);
        double blue = (-0.0041960863 * lCubed) - (0.7034186147 * mCubed) + (1.7076147010 * sCubed);

        return new Rgba(ToByte(LinearToSrgb(red)), ToByte(LinearToSrgb(green)), ToByte(LinearToSrgb(blue)), 255);
    }

    private static double LinearToSrgb(double channel)
    {
        double c = Math.Clamp(channel, 0.0, 1.0);
        return c <= 0.0031308 ? 12.92 * c : (1.055 * Math.Pow(c, 1.0 / 2.4)) - 0.055;
    }

    private static byte ToByte(double channel) => (byte)Math.Clamp(Math.Round(channel * 255.0), 0, 255);
}
