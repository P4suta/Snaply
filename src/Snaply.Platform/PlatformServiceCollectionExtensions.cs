using Microsoft.Extensions.DependencyInjection;
using Snaply.Core.Ports;

namespace Snaply.Platform;

/// <summary>
/// Composition helper that binds the headless-common Core ports to their Platform
/// adapters (capture, window enumeration, beautify renderer, export). Shared by every
/// host — the WinUI app, the CLI, and the MCP server — so the port→adapter wiring lives
/// in one place. GUI-residency services (<see cref="IHotkeyService"/>, <see cref="ITrayService"/>)
/// are deliberately excluded: they own a message-loop thread and a tray icon and belong
/// only to the WinUI app's composition root.
/// </summary>
public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the capture / window-enumeration / beautify / export adapters as singletons.
    /// They are singletons because they own native resources (a D3D/Canvas device, the shared
    /// Win2D device) that must live for the process and be shared, not re-created per use.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSnaplyPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IScreenCaptureService, WgcScreenCaptureService>();
        services.AddSingleton<IWindowEnumerationService, WindowEnumerationService>();
        services.AddSingleton<IMonitorEnumerationService, MonitorEnumerationService>();
        services.AddSingleton<IBeautifyRenderer, Win2DBeautifyRenderer>();
        services.AddSingleton<IExportService, ImageExportService>();
        services.AddSingleton<IImageImportService, ImageImportService>();
        return services;
    }
}
