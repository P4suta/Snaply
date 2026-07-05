using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snaply.Core.Ports;
using Snaply.Diagnostics;
using Snaply.Platform;
using Snaply.Services;
using Snaply.ViewModels;

namespace Snaply.Bootstrap;

/// <summary>
/// The composition root. Composes the shared layers (<c>AddSnaplyApplication</c> +
/// <c>AddSnaplyLogging</c> from Snaply.Application, <c>AddSnaplyPlatform</c> from
/// Snaply.Platform) and adds only the GUI-residency services on top — the tray icon,
/// global hotkeys, view models and presentation helpers that exist solely because this
/// host has a window. The CLI / MCP hosts reuse the same shared extensions without this
/// GUI residue.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Builds the configured service provider (composition root).</summary>
    /// <returns>A provider with logging, every port, adapter, pipeline and view model registered.</returns>
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Shared layers. The settings store is created up front so the log level can be read
        // before the logging provider exists; AddSnaplyLogging registers that same instance.
        var settingsStore = new SettingsStore();
        services.AddSnaplyApplication(settingsStore);
        services.AddSnaplyLogging(settingsStore, console: false);
        services.AddSnaplyPlatform();

        // GUI-residency services (a window, tray icon, and message-loop hotkey thread) plus the
        // presentation layer. None of these belong to the headless CLI / MCP hosts.
        services.AddSingleton<IHotkeyService, Win32HotkeyService>();
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<IUiStrings, ResourceUiStrings>();
        services.AddSingleton<ErrorPresenter>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<LanguageService>();

        ServiceProvider provider = services.BuildServiceProvider();

        // The store was created before logging existed (to read the log-level preference);
        // give it a logger now so settings read/write failures are recorded.
        settingsStore.Logger = provider.GetRequiredService<ILogger<SettingsStore>>();

        return provider;
    }
}
