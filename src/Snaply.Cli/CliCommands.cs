using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Snaply.Application;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Spectre.Console;

namespace Snaply.Cli;

/// <summary>
/// Builds the <c>snaply</c> command tree over System.CommandLine and wires each verb to the
/// shared use-case layer. Global options (<c>--json</c>/<c>--quiet</c>/<c>--verbose</c>/
/// <c>--no-color</c>) are recursive so they work on any subcommand; the beautify and output
/// options are shared instances reused across every capture verb.
/// </summary>
internal static class CliCommands
{
    private static readonly Option<bool> NoBeautifyOption = new("--no-beautify") { Description = "Skip beautify; keep the raw screenshot." };
    private static readonly Option<string?> BackgroundOption = new("--background", "-b") { Description = "auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@135 | image:<path>" };
    private static readonly Option<string?> PaddingOption = new("--padding", "-p") { Description = "Padding: N or L,T,R,B (physical px)." };
    private static readonly Option<double?> CornerRadiusOption = new("--corner-radius", "-r") { Description = "Corner radius in physical px." };
    private static readonly Option<string?> ShadowOption = new("--shadow", "-s") { Description = "none | default | offX,offY,blur,opacity[,#RRGGBB]" };
    private static readonly Option<string?> AspectOption = new("--aspect", "-a") { Description = "auto | square | standard | wide" };

    private static readonly Option<string?> OutOption = new("--out", "-o") { Description = "Save the PNG to this path." };
    private static readonly Option<bool> ClipboardOption = new("--clipboard", "-c") { Description = "Copy the PNG to the clipboard." };
    private static readonly Option<bool> StdoutOption = new("--stdout") { Description = "Write raw PNG bytes to stdout (human text goes to stderr)." };

    /// <summary>Builds the root command with every subcommand wired to <paramref name="provider"/>.</summary>
    /// <param name="provider">The composed service provider.</param>
    /// <returns>The configured root command.</returns>
    public static RootCommand BuildRoot(IServiceProvider provider)
    {
        var root = new RootCommand("Snaply — screenshot + auto-beautify from the command line.");
        Globals.AddTo(root);

        root.Subcommands.Add(BuildCapture(provider));
        root.Subcommands.Add(BuildBeautify(provider));
        root.Subcommands.Add(BuildList(provider));
        root.Subcommands.Add(DoctorCommand.Build(provider));
        root.Subcommands.Add(CompletionsCommand.Build());
        root.Subcommands.Add(McpCommand.Build());
        return root;
    }

    private static Command BuildCapture(IServiceProvider provider)
    {
        var capture = new Command("capture", "Capture the screen and (by default) beautify it.");

        var monitorOption = new Option<int>("--monitor", "-m") { Description = "Monitor index (0 = primary)." };
        var full = new Command("full", "Capture a full monitor.") { monitorOption };
        AddBeautifyAndOutput(full);
        full.SetAction((parseResult, ct) =>
            RunCaptureAsync(provider, parseResult, "capture.full", new CaptureTarget.Monitor(parseResult.GetValue(monitorOption)), ct));

        var regionArgument = new Argument<string>("region") { Description = "Region as x,y,w,h in physical pixels." };
        var region = new Command("region", "Capture a rectangular region.") { regionArgument };
        AddBeautifyAndOutput(region);
        region.SetAction(async (parseResult, ct) =>
        {
            OutputContext output = Output(parseResult);
            Result<PhysicalRect> rect = ParseRegion(parseResult.GetValue(regionArgument));
            if (rect.IsFailure)
            {
                return await output.FailAsync("capture.region", rect.Error).ConfigureAwait(true);
            }

            return await RunCaptureCoreAsync(provider, output, parseResult, "capture.region", new CaptureTarget.Region(rect.Value), ct).ConfigureAwait(true);
        });

        var hwndOption = new Option<long?>("--hwnd") { Description = "Target window handle (HWND) as a number." };
        var titleOption = new Option<string?>("--title", "-t") { Description = "Match the first window whose title contains this text." };
        var pickOption = new Option<bool>("--pick") { Description = "Interactively pick a window from a list." };
        var window = new Command("window", "Capture a single top-level window.") { hwndOption, titleOption, pickOption };
        AddBeautifyAndOutput(window);
        window.SetAction(async (parseResult, ct) =>
        {
            OutputContext output = Output(parseResult);
            Result<nint> handle = ResolveWindow(provider, output, parseResult.GetValue(hwndOption), parseResult.GetValue(titleOption), parseResult.GetValue(pickOption));
            if (handle.IsFailure)
            {
                return await output.FailAsync("capture.window", handle.Error).ConfigureAwait(true);
            }

            return await RunCaptureCoreAsync(provider, output, parseResult, "capture.window", new CaptureTarget.Window(handle.Value), ct).ConfigureAwait(true);
        });

        capture.Subcommands.Add(full);
        capture.Subcommands.Add(region);
        capture.Subcommands.Add(window);
        return capture;
    }

    private static Command BuildBeautify(IServiceProvider provider)
    {
        var inOption = new Option<string>("--in", "-i") { Description = "Input image to beautify.", Required = true };
        var beautify = new Command("beautify", "Beautify an existing image file.") { inOption };
        AddBeautifyAndOutput(beautify);
        beautify.SetAction(async (parseResult, ct) =>
        {
            OutputContext output = Output(parseResult);
            Result<BeautifySpec?> spec = MapBeautify(parseResult);
            if (spec.IsFailure)
            {
                return await output.FailAsync("beautify", spec.Error).ConfigureAwait(true);
            }

            var import = provider.GetRequiredService<IImageImportService>();
            Result<CapturedImage> raw = await import.LoadAsync(parseResult.GetValue(inOption)!, ct).ConfigureAwait(true);
            if (raw.IsFailure)
            {
                return await output.FailAsync("beautify", raw.Error).ConfigureAwait(true);
            }

            var pipeline = provider.GetRequiredService<CapturePipeline>();

            // With --no-beautify the input is re-encoded unchanged; otherwise the spec is applied.
            BeautifySpec effective = spec.Value ?? BeautifySpec.Default;
            Result<CapturedImage> result = spec.Value is null
                ? raw
                : await pipeline.BeautifyAsync(raw.Value, effective, ct).ConfigureAwait(true);
            if (result.IsFailure)
            {
                return await output.FailAsync("beautify", result.Error).ConfigureAwait(true);
            }

            return await WriteBeautifiedAsync(provider, output, parseResult, result.Value, spec.Value is not null, ct).ConfigureAwait(true);
        });
        return beautify;
    }

    private static Command BuildList(IServiceProvider provider)
    {
        var list = new Command("list", "List capture targets.");

        var windows = new Command("windows", "List capturable top-level windows (front to back).");
        windows.SetAction((parseResult, ct) =>
        {
            OutputContext output = Output(parseResult);
            IReadOnlyList<WindowInfo> found = provider.GetRequiredService<IWindowEnumerationService>().EnumerateTopLevelWindows();
            object data = found.Select(w => new
            {
                handle = Hex(w.Handle),
                title = w.Title,
                bounds = new { x = w.Bounds.X, y = w.Bounds.Y, width = w.Bounds.Width, height = w.Bounds.Height },
            }).ToArray();

            return output.SuccessAsync("list.windows", data, console =>
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("Handle");
                table.AddColumn("Size");
                table.AddColumn("Title");
                foreach (WindowInfo w in found)
                {
                    table.AddRow(
                        Markup.Escape(Hex(w.Handle)),
                        $"{w.Bounds.Width}×{w.Bounds.Height}",
                        Markup.Escape(w.Title));
                }

                console.Write(table);
            });
        });

        var monitors = new Command("monitors", "List monitors (index matches capture full --monitor).");
        monitors.SetAction((parseResult, ct) =>
        {
            OutputContext output = Output(parseResult);
            IReadOnlyList<MonitorInfo> found = provider.GetRequiredService<IMonitorEnumerationService>().EnumerateMonitors();
            object data = found.Select(m => new
            {
                index = m.Index,
                primary = m.Primary,
                dpi = m.Dpi.Value,
                bounds = new { x = m.Bounds.X, y = m.Bounds.Y, width = m.Bounds.Width, height = m.Bounds.Height },
            }).ToArray();

            return output.SuccessAsync("list.monitors", data, console =>
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("#");
                table.AddColumn("Resolution");
                table.AddColumn("DPI");
                table.AddColumn("Primary");
                foreach (MonitorInfo m in found)
                {
                    table.AddRow(
                        m.Index.ToString(CultureInfo.InvariantCulture),
                        $"{m.Bounds.Width}×{m.Bounds.Height}",
                        m.Dpi.Value.ToString("0", CultureInfo.InvariantCulture),
                        m.Primary ? "[green]yes[/]" : "[grey]no[/]");
                }

                console.Write(table);
            });
        });

        list.Subcommands.Add(windows);
        list.Subcommands.Add(monitors);
        return list;
    }

    private static void AddBeautifyAndOutput(Command command)
    {
        command.Options.Add(NoBeautifyOption);
        command.Options.Add(BackgroundOption);
        command.Options.Add(PaddingOption);
        command.Options.Add(CornerRadiusOption);
        command.Options.Add(ShadowOption);
        command.Options.Add(AspectOption);
        command.Options.Add(OutOption);
        command.Options.Add(ClipboardOption);
        command.Options.Add(StdoutOption);
    }

    private static async Task<int> RunCaptureAsync(IServiceProvider provider, ParseResult parseResult, string command, CaptureTarget target, CancellationToken ct)
    {
        OutputContext output = Output(parseResult);
        return await RunCaptureCoreAsync(provider, output, parseResult, command, target, ct).ConfigureAwait(true);
    }

    private static async Task<int> RunCaptureCoreAsync(IServiceProvider provider, OutputContext output, ParseResult parseResult, string command, CaptureTarget target, CancellationToken ct)
    {
        Result<BeautifySpec?> spec = MapBeautify(parseResult);
        if (spec.IsFailure)
        {
            return await output.FailAsync(command, spec.Error).ConfigureAwait(true);
        }

        var outputs = new OutputTargets(parseResult.GetValue(OutOption), parseResult.GetValue(ClipboardOption), parseResult.GetValue(StdoutOption));
        return await CaptureExecutor.ExecuteAsync(provider, output, command, target, spec.Value, outputs, ct).ConfigureAwait(true);
    }

    private static async Task<int> WriteBeautifiedAsync(IServiceProvider provider, OutputContext output, ParseResult parseResult, CapturedImage image, bool beautified, CancellationToken ct)
    {
        var outputs = new OutputTargets(parseResult.GetValue(OutOption), parseResult.GetValue(ClipboardOption), parseResult.GetValue(StdoutOption));
        output.RedirectHumanToErr = outputs.RawStdout;
        if (outputs is { OutPath: null, Clipboard: false, RawStdout: false })
        {
            if (output.Json)
            {
                return await output.FailAsync("beautify", new Error(ErrorCodes.OutputMissing, "No output specified. Use --out, --clipboard, or --stdout.")).ConfigureAwait(true);
            }

            outputs = outputs with { OutPath = $"snaply-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png" };
        }

        var export = provider.GetRequiredService<IExportService>();
        string? savedPath = null;
        bool copied = false;
        int? bytes = null;

        if (outputs.OutPath is not null)
        {
            Result<string> saved = await export.SavePngAsync(image, outputs.OutPath, ct).ConfigureAwait(true);
            if (saved.IsFailure)
            {
                return await output.FailAsync("beautify", saved.Error).ConfigureAwait(true);
            }

            savedPath = saved.Value;
        }

        if (outputs.Clipboard)
        {
            Result copy = await export.CopyToClipboardAsync(image, ct).ConfigureAwait(true);
            if (copy.IsFailure)
            {
                return await output.FailAsync("beautify", copy.Error).ConfigureAwait(true);
            }

            copied = true;
        }

        if (outputs.RawStdout)
        {
            Result<byte[]> encoded = await export.EncodePngAsync(image, ct).ConfigureAwait(true);
            if (encoded.IsFailure)
            {
                return await output.FailAsync("beautify", encoded.Error).ConfigureAwait(true);
            }

            await using Stream stdout = Console.OpenStandardOutput();
            await stdout.WriteAsync(encoded.Value, ct).ConfigureAwait(true);
            await stdout.FlushAsync(ct).ConfigureAwait(true);
            bytes = encoded.Value.Length;
        }

        var data = new
        {
            width = image.Size.Width,
            height = image.Size.Height,
            dpi = image.Dpi.Value,
            beautified,
            output = new { path = savedPath, clipboard = copied, stdout = outputs.RawStdout, bytes },
        };

        return await output.SuccessAsync("beautify", data, console =>
        {
            console.MarkupLine($"[green]✓[/] Beautified [bold]{image.Size.Width}×{image.Size.Height}[/]");
            if (savedPath is not null)
            {
                console.MarkupLine($"  [grey]saved[/]     {Markup.Escape(savedPath)}");
            }

            if (copied)
            {
                console.MarkupLine("  [grey]clipboard[/] copied");
            }
        }).ConfigureAwait(true);
    }

    private static Result<nint> ResolveWindow(IServiceProvider provider, OutputContext output, long? hwnd, string? title, bool pick)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        int specified = (hwnd is not null ? 1 : 0) + (hasTitle ? 1 : 0) + (pick ? 1 : 0);
        if (specified != 1)
        {
            return Result<nint>.Fail(ErrorCodes.InputInvalid, "Specify exactly one of --hwnd, --title, or --pick.");
        }

        if (hwnd is not null)
        {
            return hwnd.Value == 0
                ? Result<nint>.Fail(ErrorCodes.InputInvalid, "Window handle must be non-zero.")
                : Result<nint>.Ok((nint)hwnd.Value);
        }

        IReadOnlyList<WindowInfo> windows = provider.GetRequiredService<IWindowEnumerationService>().EnumerateTopLevelWindows();
        if (hasTitle)
        {
            WindowInfo? match = windows.FirstOrDefault(w => w.Title.Contains(title!, StringComparison.OrdinalIgnoreCase));
            return match is null
                ? Result<nint>.Fail(ErrorCodes.CaptureWindow, $"No window title contains '{title}'.")
                : Result<nint>.Ok(match.Handle);
        }

        // --pick: interactive selection is meaningless without a human console.
        if (output.Json || output.Quiet)
        {
            return Result<nint>.Fail(ErrorCodes.InputInvalid, "--pick requires an interactive terminal; use --title or --hwnd instead.");
        }

        if (windows.Count == 0)
        {
            return Result<nint>.Fail(ErrorCodes.CaptureWindow, "No capturable windows were found.");
        }

        WindowInfo chosen = output.Status.Prompt(
            new SelectionPrompt<WindowInfo>()
                .Title("Pick a [green]window[/] to capture:")
                .PageSize(15)
                .UseConverter(w => $"{Markup.Escape(Truncate(w.Title, 60))}  [grey]{w.Bounds.Width}×{w.Bounds.Height}[/]")
                .AddChoices(windows));
        return Result<nint>.Ok(chosen.Handle);
    }

    private static Result<PhysicalRect> ParseRegion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<PhysicalRect>.Fail(ErrorCodes.InputInvalid, "A region 'x,y,w,h' is required.");
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
        {
            return Result<PhysicalRect>.Fail(ErrorCodes.InputInvalid, $"Region must be four integers 'x,y,w,h' (got '{value}').");
        }

        if (w <= 0 || h <= 0)
        {
            return Result<PhysicalRect>.Fail(ErrorCodes.InputInvalid, "Region width and height must be positive.");
        }

        return Result<PhysicalRect>.Ok(new PhysicalRect(x, y, w, h));
    }

    private static OutputContext Output(ParseResult parseResult) => Globals.Output(parseResult);

    private static Result<BeautifySpec?> MapBeautify(ParseResult parseResult) => BeautifySpecMapper.Map(new BeautifyOptions(
        NoBeautify: parseResult.GetValue(NoBeautifyOption),
        Background: parseResult.GetValue(BackgroundOption),
        Padding: parseResult.GetValue(PaddingOption),
        CornerRadius: parseResult.GetValue(CornerRadiusOption),
        Shadow: parseResult.GetValue(ShadowOption),
        Aspect: parseResult.GetValue(AspectOption)));

    private static string Hex(nint handle) => "0x" + handle.ToString("X", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
