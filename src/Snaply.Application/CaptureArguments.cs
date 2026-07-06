using System.Globalization;
using Snaply.Core;
using Snaply.Core.Geometry;

namespace Snaply.Application;

/// <summary>
/// Shared parsers for the capture-targeting arguments the CLI and MCP hosts accept: window
/// handles (<c>0x…</c> hex or decimal) and <c>x,y,w,h</c> regions. Both hosts parse these
/// identically so a handle printed by <c>list windows</c> is accepted verbatim by either — the
/// one intentional difference (whether a blank handle is an error or a "fall back to the active
/// window" signal) is folded into a single flag rather than a second, drifting copy.
/// </summary>
public static class CaptureArguments
{
    /// <summary>
    /// Parses a window handle in the <c>0x…</c> hex or decimal form that <c>list windows</c>
    /// prints. A zero or malformed handle is rejected with <see cref="ErrorCodes.InputInvalid"/>.
    /// </summary>
    /// <param name="handle">The handle text (e.g. <c>0x402C4</c> or a decimal), or blank.</param>
    /// <param name="allowEmpty">
    /// When <c>true</c>, a blank/whitespace handle is a non-error result carrying <c>0</c> (the MCP
    /// tools use this so the resolver can fall back to title/process/active); when <c>false</c>, a
    /// blank handle is rejected (the CLI <c>--hwnd</c> requires a value).
    /// </param>
    /// <returns>The parsed handle, <c>0</c> for a permitted blank, or a validation failure.</returns>
    public static Result<nint> ParseWindowHandle(string? handle, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return allowEmpty
                ? Result<nint>.Ok(0)
                : Result<nint>.Fail(ErrorCodes.InputInvalid, $"Invalid window handle '{handle}'.");
        }

        string trimmed = handle.Trim();
        bool hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(
                hex ? trimmed[2..] : trimmed,
                hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long value) && value != 0
            ? Result<nint>.Ok((nint)value)
            : Result<nint>.Fail(ErrorCodes.InputInvalid, $"Invalid window handle '{handle}'.");
    }

    /// <summary>
    /// Parses a region as four integers <c>x,y,w,h</c> in physical pixels. Negative <c>x</c>/<c>y</c>
    /// are allowed (a monitor left of the primary sits at a negative virtual-desktop offset); a
    /// non-positive width or height is rejected.
    /// </summary>
    /// <param name="region">The region text, e.g. <c>10,20,300,200</c>.</param>
    /// <returns>The parsed rectangle, or a validation failure.</returns>
    public static Result<PhysicalRect> ParseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return Result<PhysicalRect>.Fail(ErrorCodes.InputInvalid, "A region 'x,y,w,h' is required.");
        }

        string[] parts = region.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
        {
            return Result<PhysicalRect>.Fail(ErrorCodes.InputInvalid, $"Region must be four integers 'x,y,w,h' (got '{region}').");
        }

        if (w <= 0 || h <= 0)
        {
            return Result<PhysicalRect>.Fail(ErrorCodes.InputInvalid, "Region width and height must be positive.");
        }

        return Result<PhysicalRect>.Ok(new PhysicalRect(x, y, w, h));
    }
}
