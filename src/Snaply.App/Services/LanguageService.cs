using Microsoft.Windows.Globalization;

namespace Snaply.Services;

/// <summary>
/// Persists the display-language choice and applies it as the process-wide
/// <see cref="ApplicationLanguages.PrimaryLanguageOverride"/>. Because MRT and
/// <c>x:Uid</c> resolve resources at load time, a change only takes full effect after
/// a restart (the Settings dialog prompts for one). <see cref="Apply"/> must run once
/// at startup, before the first window or resource is created.
/// </summary>
public sealed class LanguageService
{
    private readonly SettingsStore _store;

    /// <summary>Loads the persisted language choice (defaulting to System).</summary>
    /// <param name="store">The shared settings store.</param>
    public LanguageService(SettingsStore store)
    {
        _store = store;
        CurrentLanguage = store.Load().Language;
    }

    /// <summary>The active selection. Defaults to <see cref="AppLanguage.System"/>.</summary>
    public AppLanguage CurrentLanguage { get; private set; }

    /// <summary>
    /// Applies the persisted language as the primary override. For <see cref="AppLanguage.System"/>
    /// no override is set, so MRT resolves against the user's Windows language list and falls
    /// back to the project's DefaultLanguage (en-US) when none is supported. (The WinApp SDK
    /// override rejects an empty value, so "system" is expressed by leaving it unset.)
    /// Call once at startup before any resource is loaded.
    /// </summary>
    public void Apply()
    {
        string bcp47 = ToBcp47(CurrentLanguage);
        if (!string.IsNullOrEmpty(bcp47))
        {
            ApplicationLanguages.PrimaryLanguageOverride = bcp47;
        }
    }

    /// <summary>Records and persists a new language choice. Takes effect after a restart.</summary>
    /// <param name="language">The language to remember.</param>
    public void SetLanguage(AppLanguage language)
    {
        CurrentLanguage = language;
        _store.Update(settings => settings.Language = language);
    }

    private static string ToBcp47(AppLanguage language) => language switch
    {
        AppLanguage.English => "en-US",
        AppLanguage.Japanese => "ja-JP",
        AppLanguage.Chinese => "zh-Hans",
        _ => string.Empty, // System: clear the override so MRT uses the OS languages.
    };
}
