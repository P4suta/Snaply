using Microsoft.UI.Xaml;

namespace Snaply.Services;

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

/// <summary>
/// Applies a manual theme override to the window's root content and persists the
/// choice through the shared <see cref="SettingsStore"/>
/// (<c>%LOCALAPPDATA%\Snaply\settings.json</c>). High-contrast is handled
/// automatically by the OS because the UI uses only ThemeResource brushes.
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsStore _store;
    private FrameworkElement? _root;

    /// <summary>Loads the persisted theme choice (defaulting to System).</summary>
    /// <param name="store">The shared settings store.</param>
    public ThemeService(SettingsStore store)
    {
        _store = store;
        CurrentTheme = store.Load().Theme;
    }

    /// <summary>The active selection. Defaults to <see cref="AppTheme.System"/>.</summary>
    public AppTheme CurrentTheme { get; private set; }

    /// <summary>
    /// Bind the service to the window's root content and apply the restored theme.
    /// Call once, after the window content exists.
    /// </summary>
    /// <param name="root">The window's root content element to theme.</param>
    public void Initialize(FrameworkElement root)
    {
        _root = root;
        Apply(CurrentTheme);
    }

    /// <summary>Switch theme, apply it live, and persist the choice.</summary>
    /// <param name="theme">The theme to apply and remember.</param>
    public void SetTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        Apply(theme);
        _store.Update(settings => settings.Theme = theme);
    }

    private void Apply(AppTheme theme)
    {
        if (_root is null)
        {
            return;
        }

        _root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
