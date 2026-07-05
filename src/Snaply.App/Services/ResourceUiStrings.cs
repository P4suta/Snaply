using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Snaply.Services;

/// <summary>
/// <see cref="IUiStrings"/> backed by the app's MRT resources
/// (<c>Strings\{lang}\Resources.resw</c>) through a WinUI <see cref="ResourceLoader"/>.
/// The loader resolves the best match for the active
/// <c>ApplicationLanguages.PrimaryLanguageOverride</c> set at startup.
/// </summary>
public sealed class ResourceUiStrings : IUiStrings
{
    private readonly ResourceLoader _loader = new();

    /// <inheritdoc/>
    public string Get(string key) => _loader.GetString(key);

    /// <inheritdoc/>
    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, _loader.GetString(key), args);
}
