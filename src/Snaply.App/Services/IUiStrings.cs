namespace Snaply.Services;

/// <summary>
/// Resolves localized UI strings by resource key. Abstracts the WinUI
/// <c>ResourceLoader</c> so view models and other presentation logic stay free of
/// platform types and remain unit-testable. Backed by <see cref="ResourceUiStrings"/>.
/// </summary>
public interface IUiStrings
{
    /// <summary>Returns the localized string for <paramref name="key"/>.</summary>
    /// <param name="key">The resource key (a plain name in <c>Resources.resw</c>).</param>
    /// <returns>The localized text, or an empty string if the key is missing.</returns>
    string Get(string key);

    /// <summary>
    /// Returns the localized format string for <paramref name="key"/> filled with
    /// <paramref name="args"/> using the current UI culture.
    /// </summary>
    /// <param name="key">The resource key of a composite format string (e.g. <c>"{0} × {1} px"</c>).</param>
    /// <param name="args">The values to substitute into the format placeholders.</param>
    /// <returns>The formatted, localized text.</returns>
    string Format(string key, params object[] args);
}
