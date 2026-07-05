using System.CommandLine;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Spectre.Console;

namespace Snaply.Cli;

/// <summary>
/// The <c>snaply doctor</c> command: a colour-coded health check of the local toolchain and
/// runtime (pinned .NET SDK, mise, just, winapp, and — proven live because this very process
/// is running — the Windows App SDK capture/beautify runtime). Supports <c>--json</c> for CI.
/// </summary>
internal static class DoctorCommand
{
    private const string ExpectedSdk = "10.0.300";

    private enum Health
    {
        Ok,
        Warn,
        Fail,
    }

    /// <summary>Builds the <c>doctor</c> command.</summary>
    /// <param name="provider">The composed service provider (used to prove the capture runtime).</param>
    /// <returns>The configured command.</returns>
    public static Command Build(IServiceProvider provider)
    {
        var doctor = new Command("doctor", "Diagnose the local toolchain and capture runtime.");
        doctor.SetAction((parseResult, ct) =>
        {
            OutputContext output = Globals.Output(parseResult);
            var checks = new List<(string Name, Health Health, string Detail)>();

            (bool sdkOk, string sdkVersion) = TryRun("dotnet", "--version");
            checks.Add(("dotnet SDK", sdkOk && sdkVersion.StartsWith(ExpectedSdk, StringComparison.Ordinal) ? Health.Ok : Health.Warn,
                sdkOk ? $"{sdkVersion} (expected {ExpectedSdk})" : "dotnet not found"));

            checks.Add(Tool("mise", "--version"));
            checks.Add(Tool("just", "--version"));
            checks.Add(Tool("winapp", "--version", warnOnMissing: true));

            // The capture runtime is provable right here: if the monitor enumerator resolves and
            // returns a monitor, the Windows App SDK / WGC / DPI stack is initialized in-process.
            checks.Add(CaptureRuntime(provider));

            Health worst = checks.Count == 0 ? Health.Ok : checks.Max(c => c.Health);
            object data = new
            {
                healthy = worst != Health.Fail,
                checks = checks.Select(c => new { name = c.Name, status = StatusText(c.Health), detail = c.Detail }).ToArray(),
            };

            return output.SuccessAsync("doctor", data, console =>
            {
                console.MarkupLine("[bold]Snaply doctor[/]");
                foreach ((string name, Health health, string detail) in checks)
                {
                    (string glyph, string color) = health switch
                    {
                        Health.Ok => ("✓", "green"),
                        Health.Warn => ("!", "yellow"),
                        _ => ("✗", "red"),
                    };
                    console.MarkupLine($"  [{color}]{glyph}[/] {Markup.Escape(name)} [grey]— {Markup.Escape(detail)}[/]");
                }
            });
        });
        return doctor;
    }

    private static string StatusText(Health health) => health switch
    {
        Health.Ok => "ok",
        Health.Warn => "warn",
        _ => "fail",
    };

    private static (string Name, Health Health, string Detail) Tool(string exe, string args, bool warnOnMissing = false)
    {
        (bool ok, string version) = TryRun(exe, args);
        Health missing = warnOnMissing ? Health.Warn : Health.Fail;
        return (exe, ok ? Health.Ok : missing, ok ? version : "not found");
    }

    private static (string Name, Health Health, string Detail) CaptureRuntime(IServiceProvider provider)
    {
        try
        {
            IReadOnlyList<MonitorInfo> monitors = provider.GetRequiredService<IMonitorEnumerationService>().EnumerateMonitors();
            return ("capture runtime", monitors.Count > 0 ? Health.Ok : Health.Warn,
                monitors.Count > 0 ? $"{monitors.Count} monitor(s), Windows App SDK live" : "no monitors detected");
        }
        catch (Exception ex)
        {
            return ("capture runtime", Health.Fail, ex.Message);
        }
    }

    private static (bool Ok, string Output) TryRun(string exe, string args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return (false, string.Empty);
            }

            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            string line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
            return (process.HasExited && process.ExitCode == 0, line);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, string.Empty);
        }
    }
}
