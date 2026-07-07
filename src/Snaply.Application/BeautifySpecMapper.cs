using System.Globalization;
using Snaply.Core;
using Snaply.Core.Models;

namespace Snaply.Application;

/// <summary>
/// The raw, string-valued beautify options as they arrive from a CLI flag or an MCP tool
/// argument, before validation. A null field means "not specified — keep the default".
/// </summary>
/// <param name="Background">
/// <c>auto</c> | <c>solid:#RRGGBB[AA]</c> | <c>gradient:#RRGGBB,#RRGGBB@135</c> | <c>image:&lt;path&gt;</c>.
/// </param>
/// <param name="Padding">A single number, or four comma-separated edges <c>L,T,R,B</c> (physical px).</param>
/// <param name="CornerRadius">Corner radius in physical pixels.</param>
/// <param name="Shadow"><c>none</c> | <c>default</c> | <c>offX,offY,blur,opacity[,#RRGGBB]</c>.</param>
/// <param name="Aspect"><c>auto</c> | <c>square</c> | <c>standard</c> | <c>wide</c> (case-insensitive).</param>
public sealed record BeautifyOptions(
    string? Background = null,
    string? Padding = null,
    double? CornerRadius = null,
    string? Shadow = null,
    string? Aspect = null);

/// <summary>
/// Maps the shared string-valued <see cref="BeautifyOptions"/> (from the CLI and the MCP
/// server alike) onto the immutable Core <see cref="BeautifySpec"/>. Every unspecified
/// option keeps its <see cref="BeautifySpec.Default"/> value; a parse error becomes a
/// <see cref="Result{T}"/> failure with <see cref="ErrorCodes.InputInvalid"/> rather than
/// an exception, so both hosts render it the same way.
/// </summary>
public static class BeautifySpecMapper
{
    /// <summary>
    /// Resolves <paramref name="options"/> into a beautify spec. Every unspecified option keeps its
    /// <see cref="BeautifySpec.Default"/> value; a parse error becomes a validation failure.
    /// </summary>
    /// <param name="options">The raw beautify options to validate and map.</param>
    /// <returns>The resolved spec, or a validation failure.</returns>
    public static Result<BeautifySpec> Map(BeautifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        BeautifySpec spec = BeautifySpec.Default;

        if (options.Background is not null)
        {
            Result<Background> background = ParseBackground(options.Background);
            if (background.IsFailure)
            {
                return Result<BeautifySpec>.Fail(background.Error);
            }

            spec = spec with { Background = background.Value };
        }

        if (options.Padding is not null)
        {
            Result<Padding> padding = ParsePadding(options.Padding);
            if (padding.IsFailure)
            {
                return Result<BeautifySpec>.Fail(padding.Error);
            }

            // An explicit padding must survive the pipeline's auto-derivation.
            spec = spec with { Padding = padding.Value, AutoPadding = false };
        }

        if (options.CornerRadius is { } radius)
        {
            if (radius < 0)
            {
                return Fail($"Corner radius must be >= 0 (got {radius.ToString(CultureInfo.InvariantCulture)}).");
            }

            spec = spec with { CornerRadius = radius, AutoCornerRadius = false };
        }

        if (options.Shadow is not null)
        {
            Result<ShadowSpec> shadow = ParseShadow(options.Shadow);
            if (shadow.IsFailure)
            {
                return Result<BeautifySpec>.Fail(shadow.Error);
            }

            spec = spec with { Shadow = shadow.Value };
        }

        if (options.Aspect is not null)
        {
            if (!Enum.TryParse(options.Aspect, ignoreCase: true, out AspectPreset aspect))
            {
                return Fail($"Unknown aspect '{options.Aspect}'. Expected auto | square | standard | wide.");
            }

            spec = spec with { Aspect = aspect };
        }

        return Result<BeautifySpec>.Ok(spec);
    }

    private static Result<Background> ParseBackground(string value)
    {
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Result<Background>.Ok(new Background.Auto());
        }

        (string kind, string rest) = Split(value, ':');
        switch (kind.ToUpperInvariant())
        {
            case "SOLID":
                Result<Rgba> solid = ParseColor(rest);
                return solid.IsSuccess
                    ? Result<Background>.Ok(new Background.Solid(solid.Value))
                    : Result<Background>.Fail(solid.Error);

            case "GRADIENT":
                return ParseGradient(rest);

            case "IMAGE":
                return string.IsNullOrWhiteSpace(rest)
                    ? FailFor<Background>("Background 'image:' requires a file path.")
                    : Result<Background>.Ok(new Background.ImageFile(rest));

            default:
                return FailFor<Background>(
                    $"Unknown background '{value}'. Expected auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@deg | image:<path>.");
        }
    }

    private static Result<Background> ParseGradient(string rest)
    {
        // Format: #RRGGBB,#RRGGBB@135  (angle optional, defaults to 135°).
        double angle = 135;
        string colors = rest;
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            string angleText = rest[(at + 1)..];
            if (!double.TryParse(angleText, NumberStyles.Float, CultureInfo.InvariantCulture, out angle))
            {
                return FailFor<Background>($"Gradient angle '{angleText}' is not a number.");
            }

            colors = rest[..at];
        }

        string[] parts = colors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return FailFor<Background>("Gradient needs two colours, e.g. gradient:#1e3a8a,#9333ea@135.");
        }

        Result<Rgba> start = ParseColor(parts[0]);
        if (start.IsFailure)
        {
            return Result<Background>.Fail(start.Error);
        }

        Result<Rgba> end = ParseColor(parts[1]);
        if (end.IsFailure)
        {
            return Result<Background>.Fail(end.Error);
        }

        return Result<Background>.Ok(new Background.LinearGradient(start.Value, end.Value, angle));
    }

    private static Result<Padding> ParsePadding(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return TryParseNonNegative(parts[0], out double all)
                ? Result<Padding>.Ok(Padding.Uniform(all))
                : InvalidNumber<Padding>(parts[0], "padding");
        }

        if (parts.Length == 4)
        {
            double[] edges = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!TryParseNonNegative(parts[i], out edges[i]))
                {
                    return InvalidNumber<Padding>(parts[i], "padding");
                }
            }

            return Result<Padding>.Ok(new Padding(edges[0], edges[1], edges[2], edges[3]));
        }

        return FailFor<Padding>($"Padding must be a single number or four edges 'L,T,R,B' (got '{value}').");
    }

    private static Result<ShadowSpec> ParseShadow(string value)
    {
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ShadowSpec>.Ok(ShadowSpec.None);
        }

        if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ShadowSpec>.Ok(ShadowSpec.Default);
        }

        // Format: offX,offY,blur,opacity[,#RRGGBB]
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 4 or > 5)
        {
            return FailFor<ShadowSpec>(
                $"Shadow must be 'none', 'default', or 'offX,offY,blur,opacity[,#RRGGBB]' (got '{value}').");
        }

        if (!TryParseDouble(parts[0], out double offX)
            || !TryParseDouble(parts[1], out double offY)
            || !TryParseDouble(parts[2], out double blur)
            || !TryParseDouble(parts[3], out double opacity))
        {
            return FailFor<ShadowSpec>($"Shadow numbers could not be parsed from '{value}'.");
        }

        if (blur < 0)
        {
            return FailFor<ShadowSpec>("Shadow blur must be >= 0.");
        }

        opacity = Math.Clamp(opacity, 0, 1);
        Rgba color = new(0, 0, 0, 255);
        if (parts.Length == 5)
        {
            Result<Rgba> parsed = ParseColor(parts[4]);
            if (parsed.IsFailure)
            {
                return Result<ShadowSpec>.Fail(parsed.Error);
            }

            color = parsed.Value;
        }

        return Result<ShadowSpec>.Ok(new ShadowSpec(offX, offY, blur, opacity, color));
    }

    /// <summary>
    /// Parses a hex colour: <c>#RGB</c>, <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> (the leading
    /// <c>#</c> is optional). Shared by the background and shadow parsers.
    /// </summary>
    /// <param name="value">The hex colour text.</param>
    /// <returns>The parsed colour, or a validation failure.</returns>
    public static Result<Rgba> ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FailFor<Rgba>("A colour value is required.");
        }

        string hex = value.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            // #RGB -> #RRGGBB
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if ((hex.Length != 6 && hex.Length != 8)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
        {
            return FailFor<Rgba>($"Colour '{value}' must be #RGB, #RRGGBB, or #RRGGBBAA.");
        }

        byte a = 255;
        if (hex.Length == 8)
        {
            a = (byte)(packed & 0xFF);
            packed >>= 8;
        }

        byte r = (byte)((packed >> 16) & 0xFF);
        byte g = (byte)((packed >> 8) & 0xFF);
        byte b = (byte)(packed & 0xFF);
        return Result<Rgba>.Ok(new Rgba(r, g, b, a));
    }

    private static (string Head, string Tail) Split(string value, char separator)
    {
        int i = value.IndexOf(separator, StringComparison.Ordinal);
        return i < 0 ? (value, string.Empty) : (value[..i], value[(i + 1)..]);
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseNonNegative(string text, out double value) =>
        TryParseDouble(text, out value) && value >= 0;

    private static Result<T> InvalidNumber<T>(string text, string what) =>
        FailFor<T>($"Invalid {what} value '{text}' (must be a non-negative number).");

    private static Result<T> FailFor<T>(string message) =>
        Result<T>.Fail(ErrorCodes.InputInvalid, message);

    private static Result<BeautifySpec> Fail(string message) =>
        Result<BeautifySpec>.Fail(ErrorCodes.InputInvalid, message);
}
