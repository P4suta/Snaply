using Microsoft.Extensions.DependencyInjection;
using Snaply.Diagnostics;
using Snaply.Platform;
using Snaply.Services;
using Snaply.ViewModels;

namespace Snaply.Bootstrap;

/// <summary>
/// The composition root. Composes the shared layers (<c>AddSnaplyApplication</c> +
/// <c>AddSnaplyLogging</c> from Snaply.Application, <c>AddSnaplyPlatform</c> from
/// Snaply.Platform) and adds only the presentation services on top — the view model and
/// UI helpers that exist solely because this host has a window. The CLI / MCP hosts reuse
/// the same shared extensions without this GUI residue.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Builds the configured service provider (composition root).</summary>
    /// <returns>A provider with logging, every port, adapter, pipeline and view model registered.</returns>
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Shared layers (capture/beautify pipeline + file-only logging).
        services.AddSnaplyApplication();
        services.AddSnaplyLogging(console: false);
        services.AddSnaplyPlatform();

        // Presentation layer — the view model and UI-string resolver that exist only because
        // this host has a window. None of these belong to the headless CLI / MCP hosts.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<IUiStrings, ResourceUiStrings>();
        services.AddSingleton<ErrorPresenter>();

        return services.BuildServiceProvider();
    }
}
