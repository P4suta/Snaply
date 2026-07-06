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
    private static readonly Option<int> DelayOption = new("--delay") { Description = "Wait this many milliseconds before capturing (open a menu/dialog first)." };

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
        full.Options.Add(DelayOption);
        full.SetAction((parseResult, ct) =>
            RunCaptureAsync(provider, parseResult, "capture.full", new CaptureTarget.Monitor(parseResult.GetValue(monitorOption)), ct));

        var regionArgument = new Argument<string>("region") { Description = "Region as x,y,w,h in physical pixels." };
        var region = new Command("region", "Capture a rectangular region.") { regionArgument };
        AddBeautifyAndOutput(region);
        region.Options.Add(DelayOption);
        region.SetAction(async (parseResult, ct) =>
        {
            OutputContext output = Output(parseResult);
            Result<PhysicalRect> rect = CaptureArguments.ParseRegion(parseResult.GetValue(regionArgument));
            if (rect.IsFailure)
            {
                return await output.FailAsync("capture.region", rect.Error).ConfigureAwait(true);
            }

            return await RunCaptureCoreAsync(provider, output, parseResult, "capture.region", new CaptureTarget.Region(rect.Value), ct).ConfigureAwait(true);
        });

        var hwndOption = new Option<string?>("--hwnd") { Description = "Target window handle (HWND), e.g. 0x402C4 or decimal (from 'list windows')." };
        var titleOption = new Option<string?>("--title", "-t") { Description = "Match windows whose title contains this text." };
        var processOption = new Option<string?>("--process", "-P") { Description = "Match windows owned by this process (name, '.exe' optional)." };
        var activeOption = new Option<bool>("--active") { Description = "Capture the current foreground window (the default with no selector)." };
        var pickOption = new Option<bool>("--pick") { Description = "Interactively pick a window from a list." };
        var withPopupsOption = new Option<bool>("--with-popups") { Description = "Include the window's dialogs/popups (file picker, menus) in one shot." };
        var window = new Command("window", "Capture a single top-level window (or the active one).")
        {
            hwndOption, titleOption, processOption, activeOption, pickOption, withPopupsOption,
        };
        AddBeautifyAndOutput(window);
        window.Options.Add(DelayOption);
        window.SetAction(async (parseResult, ct) =>
        {
            OutputContext output = Output(parseResult);
            Result<nint> handle = ResolveWindow(
                provider,
                output,
                parseResult.GetValue(hwndOption),
                parseResult.GetValue(titleOption),
                parseResult.GetValue(processOption),
                parseResult.GetValue(activeOption),
                parseResult.GetValue(pickOption));
            if (handle.IsFailure)
            {
                return await output.FailAsync("capture.window", handle.Error).ConfigureAwait(true);
            }

            // --with-popups composes the window with its owned dialogs/popups into one region so a
            // file picker sitting in front of the app is captured too (a single-window capture would
            // only show the app's own surface).
            CaptureTarget target;
            if (parseResult.GetValue(withPopupsOption))
            {
                Result<PhysicalRect> group = provider.GetRequiredService<WindowResolver>().ResolveGroupRegion(handle.Value);
                if (group.IsFailure)
                {
                    return await output.FailAsync("capture.window", group.Error).ConfigureAwait(true);
                }

                target = new CaptureTarget.Region(group.Value);
            }
            else
            {
                target = new CaptureTarget.Window(handle.Value);
            }

            return await RunCaptureCoreAsync(provider, output, parseResult, "capture.window", target, ct).ConfigureAwait(true);
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
                processName = w.ProcessName,
                processId = w.ProcessId,
                className = w.ClassName,
                owner = w.OwnerHandle == 0 ? null : Hex(w.OwnerHandle),
                foreground = w.IsForeground,
                bounds = new { x = w.Bounds.X, y = w.Bounds.Y, width = w.Bounds.Width, height = w.Bounds.Height },
            }).ToArray();

            return output.SuccessAsync("list.windows", data, console =>
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("Handle");
                table.AddColumn("Process");
                table.AddColumn("Size");
                table.AddColumn("Title");
                foreach (WindowInfo w in found)
                {
                    string process = w.ProcessName.Length == 0 ? "[grey]?[/]" : Markup.Escape(w.ProcessName);
                    string marker = w.IsForeground ? " [green]●[/]" : string.Empty;
                    table.AddRow(
                        Markup.Escape(Hex(w.Handle)) + marker,
                        process,
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

        // Every capture verb carries --delay; the beautify verb writes via its own path, not here.
        return await CaptureExecutor.ExecuteAsync(provider, output, command, target, spec.Value, outputs, parseResult.GetValue(DelayOption), ct).ConfigureAwait(true);
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

    private static Result<nint> ResolveWindow(IServiceProvider provider, OutputContext output, string? hwnd, string? title, string? process, bool active, bool pick)
    {
        bool hasHandle = !string.IsNullOrWhiteSpace(hwnd);
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        bool hasProcess = !string.IsNullOrWhiteSpace(process);

        // --pick / --hwnd / --active are alternatives; --title and --process are filters that can be
        // combined with each other but not with an alternative.
        int alternatives = (hasHandle ? 1 : 0) + (active ? 1 : 0) + (pick ? 1 : 0);
        if (alternatives > 1 || (alternatives == 1 && (hasTitle || hasProcess)))
        {
            return Result<nint>.Fail(ErrorCodes.InputInvalid, "Use one of --hwnd, --active, --pick, or a --title/--process filter — not a mix.");
        }

        if (pick)
        {
            return PickWindow(provider, output);
        }

        nint? handle = null;
        if (hasHandle)
        {
            Result<nint> parsed = CaptureArguments.ParseWindowHandle(hwnd!, allowEmpty: false);
            if (parsed.IsFailure)
            {
                return parsed;
            }

            handle = parsed.Value;
        }

        var resolver = provider.GetRequiredService<WindowResolver>();
        WindowResolution resolution = resolver.Resolve(new WindowQuery(
            Handle: handle,
            TitleContains: title,
            ProcessName: process,
            Active: active));

        switch (resolution)
        {
            case WindowResolution.Resolved resolved:
                return Result<nint>.Ok(resolved.Window.Handle);

            case WindowResolution.Ambiguous ambiguous:
                RenderCandidates(output, ambiguous.Candidates);
                return Result<nint>.Fail(ErrorCodes.CaptureWindowAmbiguous, DescribeCandidates(ambiguous.Candidates));

            case WindowResolution.NotFound notFound:
                return Result<nint>.Fail(notFound.Error);

            default:
                return Result<nint>.Fail(ErrorCodes.CaptureWindow, "Could not resolve a window.");
        }
    }

    private static Result<nint> PickWindow(IServiceProvider provider, OutputContext output)
    {
        // Interactive selection is meaningless without a human console.
        if (output.Json || output.Quiet)
        {
            return Result<nint>.Fail(ErrorCodes.InputInvalid, "--pick requires an interactive terminal; use --title, --process, or --hwnd instead.");
        }

        IReadOnlyList<WindowInfo> windows = provider.GetRequiredService<IWindowEnumerationService>().EnumerateTopLevelWindows();
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

    private static void RenderCandidates(OutputContext output, IReadOnlyList<WindowInfo> candidates)
    {
        if (output.Json || output.Quiet)
        {
            return;
        }

        output.Status.MarkupLine("[yellow]Several windows matched — re-run with --hwnd <handle>:[/]");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Handle");
        table.AddColumn("Process");
        table.AddColumn("Title");
        foreach (WindowInfo w in candidates)
        {
            table.AddRow(
                Markup.Escape(Hex(w.Handle)),
                w.ProcessName.Length == 0 ? "[grey]?[/]" : Markup.Escape(w.ProcessName),
                Markup.Escape(Truncate(w.Title, 60)));
        }

        output.Status.Write(table);
    }

    private static string DescribeCandidates(IReadOnlyList<WindowInfo> candidates)
    {
        IEnumerable<string> listed = candidates.Take(8).Select(w => $"{Hex(w.Handle)} '{Truncate(w.Title, 40)}' ({w.ProcessName})");
        string suffix = candidates.Count > 8 ? $", +{candidates.Count - 8} more" : string.Empty;
        return $"{candidates.Count} windows matched: {string.Join("; ", listed)}{suffix}. Re-run with --hwnd <handle>.";
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
