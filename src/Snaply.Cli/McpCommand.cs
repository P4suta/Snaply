using System.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snaply.Application;
using Snaply.Cli.Mcp;
using Snaply.Platform;

namespace Snaply.Cli;

/// <summary>
/// The <c>snaply mcp serve</c> command: a Model Context Protocol server that exposes Snaply's
/// capture/beautify use cases to AI clients. Two transports share one tool set:
/// <list type="bullet">
/// <item><c>stdio</c> (default) — the classic local transport used by desktop clients
/// (Claude Desktop / Code).</item>
/// <item><c>http</c> — the modern <b>Streamable HTTP</b> transport (an ASP.NET Core endpoint)
/// for networked/remote AI clients; supersedes the deprecated HTTP+SSE transport.</item>
/// </list>
/// It composes the same shared Application + Platform services the CLI uses.
/// </summary>
internal static class McpCommand
{
    /// <summary>Builds the <c>mcp</c> command.</summary>
    /// <returns>The configured command.</returns>
    public static Command Build()
    {
        var allowCaptureOption = new Option<bool>("--allow-capture") { Description = "Permit the screen-capture tools (off by default)." };
        var consentModeOption = new Option<string>("--consent-mode") { Description = "deny | prompt-once | allow (how capture is authorized).", DefaultValueFactory = _ => "prompt-once" };
        consentModeOption.AcceptOnlyFromAmong("deny", "prompt-once", "allow");

        var transportOption = new Option<string>("--transport") { Description = "stdio (local desktop clients) | http (modern Streamable HTTP).", DefaultValueFactory = _ => "stdio" };
        transportOption.AcceptOnlyFromAmong("stdio", "http");
        var httpUrlOption = new Option<string>("--http-url") { Description = "Listen URL for --transport http.", DefaultValueFactory = _ => "http://localhost:3001" };
        var statelessOption = new Option<bool>("--stateless") { Description = "Stateless Streamable HTTP (no per-session state; easier to scale)." };

        var serve = new Command("serve", "Run the MCP server (stdio or Streamable HTTP).")
        {
            allowCaptureOption, consentModeOption, transportOption, httpUrlOption, statelessOption,
        };
        serve.SetAction((parseResult, ct) => ServeAsync(
            parseResult.GetValue(allowCaptureOption),
            parseResult.GetValue(consentModeOption)!,
            parseResult.GetValue(transportOption)!,
            parseResult.GetValue(httpUrlOption)!,
            parseResult.GetValue(statelessOption),
            ct));

        return new Command("mcp", "Model Context Protocol server for AI clients.") { serve };
    }

    private static Task<int> ServeAsync(bool allowCapture, string consentMode, string transport, string httpUrl, bool stateless, CancellationToken ct)
    {
        var policy = CapturePolicy.From(allowCapture, consentMode);
        return string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
            ? ServeHttpAsync(policy, httpUrl, stateless, ct)
            : ServeStdioAsync(policy, ct);
    }

    private static async Task<int> ServeStdioAsync(CapturePolicy policy, CancellationToken ct)
    {
        var settingsStore = new SettingsStore();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // stdout is the MCP protocol channel — never let a console logger write to it.
        builder.Logging.ClearProviders();
        AddSnaplyServices(builder.Services, settingsStore, policy);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<SnaplyMcpTools>();

        IHost host = builder.Build();
        settingsStore.Logger = host.Services.GetRequiredService<ILogger<SettingsStore>>();
        await host.RunAsync(ct).ConfigureAwait(true);
        return ExitCodes.Success;
    }

    private static async Task<int> ServeHttpAsync(CapturePolicy policy, string httpUrl, bool stateless, CancellationToken ct)
    {
        var settingsStore = new SettingsStore();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());

        AddSnaplyServices(builder.Services, settingsStore, policy);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = stateless)
            .WithTools<SnaplyMcpTools>();

        WebApplication app = builder.Build();
        app.Urls.Add(httpUrl);
        settingsStore.Logger = app.Services.GetRequiredService<ILogger<SettingsStore>>();

        // DNS-rebinding / cross-origin defence: the MCP spec requires HTTP servers to validate the
        // Host (and Origin) header. Accept only the bound host + loopback, so a browser page rebound
        // to 127.0.0.1 (whose Host/Origin is the attacker's domain) cannot reach the capture tools.
        string expectedHost = Uri.TryCreate(httpUrl, UriKind.Absolute, out Uri? bound) ? bound.Host : "localhost";
        app.Use(async (context, next) =>
        {
            if (!IsAllowedHost(context.Request, expectedHost))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: host/origin not allowed.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        // Map the Streamable HTTP endpoint at the root path.
        app.MapMcp();

        await Console.Error.WriteLineAsync($"Snaply MCP (Streamable HTTP) listening on {httpUrl} — capture {(policy.AllowCapture ? "enabled" : "disabled")}").ConfigureAwait(true);
        await app.RunAsync(ct).ConfigureAwait(true);
        return ExitCodes.Success;
    }

    private static bool IsAllowedHost(HttpRequest request, string expectedHost)
    {
        if (!IsHostAllowed(request.Host.Host, expectedHost))
        {
            return false;
        }

        // If the request carries an Origin (browser-issued), it must resolve to an allowed host too.
        string origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            return Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed) && IsHostAllowed(parsed.Host, expectedHost);
        }

        return true;
    }

    private static bool IsHostAllowed(string? host, string expectedHost) =>
        host is not null
        && (host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "::1" or "[::1]");

    private static void AddSnaplyServices(IServiceCollection services, SettingsStore settingsStore, CapturePolicy policy)
    {
        services.AddSnaplyApplication(settingsStore);
        services.AddSnaplyLogging(settingsStore, console: false);
        services.AddSnaplyPlatform();
        services.AddSingleton(policy);
    }
}
