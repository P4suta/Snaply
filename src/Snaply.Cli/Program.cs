using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snaply.Application;
using Snaply.Platform;

namespace Snaply.Cli;

/// <summary>Entry point for the <c>snaply</c> CLI.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The MCP server is a long-lived stdio host and never touches the clipboard, so it must
        // NOT run inside the STA message pump (the pump and the host's stdio I/O interfere). Every
        // other verb may use the clipboard, so it runs on the pump host (see CliHost). WGC capture
        // and Win2D rendering are free-threaded, so they work fine on either path.
        if (args is ["mcp", ..])
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }

        return CliHost.Run(() => RunAsync(args));
    }

    private static async Task<int> RunAsync(string[] args)
    {
        ServiceProvider provider = BuildServices();
        RootCommand root = CliCommands.BuildRoot(provider);
        return await root.Parse(args).InvokeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Composes the CLI's services: the shared Application use cases + the Platform adapters,
    /// with file-only logging (Serilog to <c>%LOCALAPPDATA%\Snaply\logs</c>) so diagnostics are
    /// captured without cluttering the console — the CLI's own output is owned by Spectre.
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var settingsStore = new SettingsStore();
        var services = new ServiceCollection();
        services.AddSnaplyApplication(settingsStore);
        services.AddSnaplyLogging(settingsStore, console: false);
        services.AddSnaplyPlatform();
        ServiceProvider provider = services.BuildServiceProvider();
        settingsStore.Logger = provider.GetRequiredService<ILogger<SettingsStore>>();
        return provider;
    }
}
