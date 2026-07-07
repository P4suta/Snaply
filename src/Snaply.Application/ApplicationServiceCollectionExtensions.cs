using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Snaply.Application;

/// <summary>
/// Composition helpers for the shared Application layer. Both the WinUI app and the CLI
/// call these so the use-case orchestration (<see cref="CapturePipeline"/>) and the
/// rolling-JSON logging story are wired identically regardless of which host is running.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Composition root + Serilog wiring; exercised by hosts, not unit-tested.")]
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the capture/beautify orchestration pipeline (a singleton: it caches the last raw
    /// capture for live re-render) and the shared window resolver.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSnaplyApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<CapturePipeline>();
        services.AddSingleton<WindowResolver>();
        return services;
    }

    /// <summary>
    /// Registers Serilog-backed logging: a rolling daily JSON-lines file under
    /// <c>%LOCALAPPDATA%\Snaply\logs</c> for both hosts, plus an optional console sink
    /// (written to <c>stderr</c> so it never corrupts a CLI's <c>stdout</c> data or the
    /// MCP stdio protocol channel). The level is held by a shared
    /// <see cref="LoggingLevelSwitch"/> (Information), left adjustable at runtime.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="console">When true, also write to the console (stderr).</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSnaplyLogging(this IServiceCollection services, bool console)
    {
        ArgumentNullException.ThrowIfNull(services);

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        services.AddSingleton(levelSwitch);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(CreateLogger(levelSwitch, console), dispose: true);
        });

        return services;
    }

    // Rolling daily JSON-lines log under %LOCALAPPDATA%\Snaply\logs, level controlled live by
    // the shared LoggingLevelSwitch. Enriched with the app version so every line is
    // self-describing. Ownership transfers to the logging provider (dispose: true at the call
    // site), which disposes it on shutdown. The optional console sink targets stderr so stdout
    // stays clean for piped PNG bytes / MCP frames.
    private static Logger CreateLogger(LoggingLevelSwitch levelSwitch, bool console)
    {
        string logDirectory = AppPaths.Ensure(AppPaths.LogsDirectory);
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.WithProperty("app", "Snaply")
            .Enrich.WithProperty("version", version)
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logDirectory, "snaply-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true);

        if (console)
        {
            configuration = configuration.WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                formatProvider: CultureInfo.InvariantCulture);
        }

        return configuration.CreateLogger();
    }
}
