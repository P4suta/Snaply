using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Snaply.Core.Ports;
using Snaply.Diagnostics;
using Snaply.Platform;
using Snaply.Services;
using Snaply.ViewModels;

namespace Snaply.Bootstrap;

/// <summary>
/// The composition root. Binds each Core port to its Platform adapter and wires up
/// the app-level orchestration + ViewModels. Capture / renderer / export / hotkey /
/// tray are singletons: they own native resources (D3D device, message-loop thread,
/// tray icon) that must live for the whole app and be shared, not re-created per use.
/// Observability (Serilog) is registered first so every service can take an <c>ILogger&lt;T&gt;</c>.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Builds the configured service provider (composition root).</summary>
    /// <returns>A provider with logging, every port, adapter, pipeline and view model registered.</returns>
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Observability first. Reuse a single SettingsStore instance so the verbose-logging
        // preference is read once and the same store is shared with the rest of the app.
        var settingsStore = new SettingsStore();
        var levelSwitch = new LoggingLevelSwitch(
            settingsStore.Load().VerboseLogging ? LogEventLevel.Debug : LogEventLevel.Information);

        services.AddSingleton(settingsStore);
        services.AddSingleton(levelSwitch);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(CreateLogger(levelSwitch), dispose: true);
        });

        // Core ports -> Platform adapters.
        services.AddSingleton<IScreenCaptureService, WgcScreenCaptureService>();
        services.AddSingleton<IWindowEnumerationService, WindowEnumerationService>();
        services.AddSingleton<IBeautifyRenderer, Win2DBeautifyRenderer>();
        services.AddSingleton<IExportService, ImageExportService>();
        services.AddSingleton<IHotkeyService, Win32HotkeyService>();
        services.AddSingleton<ITrayService, TrayService>();

        // App-level orchestration + view models (single shared VM so the tray/hotkey
        // paths drive the same preview the window shows).
        services.AddSingleton<CapturePipeline>();
        services.AddSingleton<MainViewModel>();

        // Presentation-only services (no Core dependency).
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

    // Rolling daily JSON-lines log under %LOCALAPPDATA%\Snaply\logs, level controlled live by
    // the shared LoggingLevelSwitch (toggled from the diagnostics panel). Enriched with the app
    // version so every line is self-describing. Ownership transfers to the logging provider
    // (dispose: true at the call site), which disposes it on shutdown.
    private static Logger CreateLogger(LoggingLevelSwitch levelSwitch)
    {
        string logDirectory = AppPaths.Ensure(AppPaths.LogsDirectory);
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.WithProperty("app", "Snaply")
            .Enrich.WithProperty("version", version)
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logDirectory, "snaply-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }
}
