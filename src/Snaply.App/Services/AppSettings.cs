namespace Snaply.Services;

/// <summary>
/// The full set of user preferences persisted to
/// <c>%LOCALAPPDATA%\Snaply\settings.json</c>. Kept as a single document so every
/// service reads and writes through one shared <see cref="SettingsStore"/>, which
/// prevents one service's save from clobbering another service's field.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The selected theme override. Defaults to <see cref="AppTheme.System"/>.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>The selected display language. Defaults to <see cref="AppLanguage.System"/>.</summary>
    public AppLanguage Language { get; set; } = AppLanguage.System;

    /// <summary>
    /// When true, logging is raised to Debug level for deeper diagnostics. Defaults to false
    /// (Information). Toggled live from the Settings diagnostics panel.
    /// </summary>
    public bool VerboseLogging { get; set; }
}
