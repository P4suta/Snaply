using System.Diagnostics.CodeAnalysis;

namespace Snaply.Application;

/// <summary>The user-selectable app theme. <see cref="System"/> follows the OS.</summary>
public enum AppTheme
{
    /// <summary>Follow the OS theme.</summary>
    System,

    /// <summary>Force the light theme.</summary>
    Light,

    /// <summary>Force the dark theme.</summary>
    Dark,
}

/// <summary>The user-selectable display language. <see cref="System"/> follows the OS.</summary>
public enum AppLanguage
{
    /// <summary>Follow the operating system's display languages (MRT picks the best match).</summary>
    System,

    /// <summary>English (en-US).</summary>
    English,

    /// <summary>Japanese (ja-JP).</summary>
    Japanese,

    /// <summary>Simplified Chinese (zh-Hans).</summary>
    Chinese,
}

/// <summary>
/// The full set of user preferences persisted to
/// <c>%LOCALAPPDATA%\Snaply\settings.json</c>. Kept as a single document so every
/// service reads and writes through one shared <see cref="SettingsStore"/>, which
/// prevents one service's save from clobbering another service's field.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Serializable settings DTO (auto-properties); no logic to unit-test.")]
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
