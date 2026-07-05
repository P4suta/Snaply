using Snaply.Core;
using Snaply.Services;

namespace Snaply.Diagnostics;

/// <summary>A user-facing rendering of a failure: a friendly localized title plus raw technical detail.</summary>
/// <param name="Title">The localized, user-friendly message shown prominently.</param>
/// <param name="Detail">The raw technical detail (code, message, exception summary) for the opt-in "details" view.</param>
public sealed record PresentedError(string Title, string Detail);

/// <summary>
/// Maps a Core <see cref="Error"/> to a localized, user-friendly message (keyed by
/// <see cref="Error.Code"/> against the resw resource <c>error.&lt;code&gt;</c>) while preserving the
/// raw technical detail for an opt-in "details" expander. Unknown codes fall back to
/// <c>error.unknown</c>. This keeps raw platform text (HRESULT strings etc.) out of the
/// primary UI while remaining one click away for diagnosis.
/// </summary>
public sealed class ErrorPresenter
{
    private readonly IUiStrings _strings;

    /// <summary>Creates the presenter over the localized string resolver.</summary>
    /// <param name="strings">The localized UI string resolver.</param>
    public ErrorPresenter(IUiStrings strings) => _strings = strings;

    /// <summary>Renders <paramref name="error"/> into a friendly title and a raw detail string.</summary>
    /// <param name="error">The failure to present.</param>
    /// <returns>The localized title and technical detail.</returns>
    public PresentedError Present(Error error)
    {
        // Resource names use '_' (a '.' in a resw name is treated as a scope separator by MRT,
        // so a dotted key like "error.export.save" is not retrievable via GetString).
        string friendly = _strings.Get("Error_" + error.Code.Replace('.', '_'));
        if (string.IsNullOrEmpty(friendly))
        {
            friendly = _strings.Get("Error_unknown");
        }

        string detail = error.Cause is { } cause
            ? $"[{error.Code}] {error.Message}\n{cause.GetType().Name}: {cause.Message}"
            : $"[{error.Code}] {error.Message}";

        return new PresentedError(friendly, detail);
    }
}
