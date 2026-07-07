using Microsoft.Extensions.DependencyInjection;
using Snaply.Application;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Spectre.Console;

namespace Snaply.Cli;

/// <summary>What to capture: a monitor, a region, or a window.</summary>
internal abstract record CaptureTarget
{
    private CaptureTarget()
    {
    }

    /// <summary>Capture a full monitor by index (0 == primary).</summary>
    /// <param name="Index">Zero-based monitor index.</param>
    public sealed record Monitor(int Index) : CaptureTarget;

    /// <summary>Capture a physical-pixel region of the virtual desktop.</summary>
    /// <param name="Rect">The region in physical pixels.</param>
    public sealed record Region(PhysicalRect Rect) : CaptureTarget;

    /// <summary>Capture a single top-level window by handle.</summary>
    /// <param name="Handle">The native window handle (HWND).</param>
    public sealed record Window(nint Handle) : CaptureTarget;
}

/// <summary>Where a capture's PNG should go. Any combination may be requested.</summary>
/// <param name="OutPath">A file path to save to, or null.</param>
/// <param name="Clipboard">Whether to copy to the clipboard.</param>
/// <param name="RawStdout">Whether to stream the raw PNG bytes to stdout.</param>
internal sealed record OutputTargets(string? OutPath, bool Clipboard, bool RawStdout);

/// <summary>
/// Runs a capture request end to end: capture (raw or beautified) → the requested outputs
/// (file / clipboard / stdout) → a rich human summary or a JSON envelope. Shared by every
/// <c>capture</c> verb and the <c>beautify</c> verb so the output/exit-code behaviour is uniform.
/// </summary>
internal static class CaptureExecutor
{
    /// <summary>Executes a capture and writes its outputs, returning the process exit code.</summary>
    /// <param name="provider">The composed service provider.</param>
    /// <param name="output">The output context (human/JSON rendering).</param>
    /// <param name="command">The dotted command name (e.g. <c>capture.region</c>).</param>
    /// <param name="target">What to capture.</param>
    /// <param name="spec">The beautify spec to apply.</param>
    /// <param name="outputs">Where to send the resulting PNG.</param>
    /// <param name="delayMs">Milliseconds to wait before capturing (0 = none).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ExecuteAsync(
        IServiceProvider provider,
        OutputContext output,
        string command,
        CaptureTarget target,
        BeautifySpec spec,
        OutputTargets outputs,
        int delayMs,
        CancellationToken ct)
    {
        output.RedirectHumanToErr = outputs.RawStdout;

        // In machine mode, refuse to invent a file path; a human run defaults to a timestamped file.
        if (outputs is { OutPath: null, Clipboard: false, RawStdout: false })
        {
            if (output.Json)
            {
                return await output.FailAsync(
                    command,
                    new Error(ErrorCodes.OutputMissing, "No output specified. Use --out, --clipboard, or --stdout.")).ConfigureAwait(true);
            }

            outputs = outputs with { OutPath = DefaultFileName() };
        }

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(true);
        }

        Result<CapturedImage> captured = await WithStatusAsync(
            output,
            "Capturing & beautifying…",
            () => CaptureAsync(provider, target, spec, ct)).ConfigureAwait(true);

        if (captured.IsFailure)
        {
            return await output.FailAsync(command, captured.Error).ConfigureAwait(true);
        }

        CapturedImage image = captured.Value;
        var export = provider.GetRequiredService<IExportService>();
        string? savedPath = null;
        bool copied = false;
        int? bytes = null;

        if (outputs.OutPath is not null)
        {
            Result<string> saved = await export.SavePngAsync(image, outputs.OutPath, ct).ConfigureAwait(true);
            if (saved.IsFailure)
            {
                return await output.FailAsync(command, saved.Error).ConfigureAwait(true);
            }

            savedPath = saved.Value;
            try
            {
                bytes = (int)new FileInfo(saved.Value).Length;
            }
            catch (IOException)
            {
                // Size is a nicety; never fail the command over it.
            }
        }

        if (outputs.Clipboard)
        {
            Result copy = await export.CopyToClipboardAsync(image, ct).ConfigureAwait(true);
            if (copy.IsFailure)
            {
                return await output.FailAsync(command, copy.Error).ConfigureAwait(true);
            }

            copied = true;
        }

        if (outputs.RawStdout)
        {
            Result<byte[]> encoded = await export.EncodePngAsync(image, ct).ConfigureAwait(true);
            if (encoded.IsFailure)
            {
                return await output.FailAsync(command, encoded.Error).ConfigureAwait(true);
            }

            await using Stream stdout = Console.OpenStandardOutput();
            await stdout.WriteAsync(encoded.Value, ct).ConfigureAwait(true);
            await stdout.FlushAsync(ct).ConfigureAwait(true);
            bytes ??= encoded.Value.Length;
        }

        var data = new
        {
            width = image.Size.Width,
            height = image.Size.Height,
            dpi = image.Dpi.Value,
            beautified = true,
            output = new
            {
                path = savedPath,
                clipboard = copied,
                stdout = outputs.RawStdout,
                bytes,
            },
        };

        return await output.SuccessAsync(command, data, console =>
            RenderSummary(console, image, savedPath, copied, outputs.RawStdout)).ConfigureAwait(true);
    }

    private static async Task<Result<CapturedImage>> CaptureAsync(
        IServiceProvider provider, CaptureTarget target, BeautifySpec spec, CancellationToken ct)
    {
        var pipeline = provider.GetRequiredService<CapturePipeline>();
        return target switch
        {
            CaptureTarget.Monitor m => await pipeline.CaptureMonitorAsync(m.Index, spec, ct).ConfigureAwait(true),
            CaptureTarget.Region r => await pipeline.CaptureRegionAsync(r.Rect, spec, ct).ConfigureAwait(true),
            CaptureTarget.Window w => await pipeline.CaptureWindowAsync(w.Handle, spec, ct).ConfigureAwait(true),
            _ => Result<CapturedImage>.Fail(ErrorCodes.InputInvalid, "Unknown capture target."),
        };
    }

    private static void RenderSummary(IAnsiConsole console, CapturedImage image, string? savedPath, bool copied, bool rawStdout)
    {
        console.MarkupLine($"[green]✓[/] Captured [bold]{image.Size.Width}×{image.Size.Height}[/] @ {image.Dpi.Value:0}dpi ([green]beautified[/])");
        if (savedPath is not null)
        {
            console.MarkupLine($"  [grey]saved[/]     {Markup.Escape(savedPath)}");
        }

        if (copied)
        {
            console.MarkupLine("  [grey]clipboard[/] copied");
        }

        if (rawStdout)
        {
            console.MarkupLine("  [grey]stdout[/]    raw PNG bytes");
        }
    }

    private static async Task<T> WithStatusAsync<T>(OutputContext output, string message, Func<Task<T>> work)
    {
        if (output.Json || output.Quiet)
        {
            return await work().ConfigureAwait(true);
        }

        return await output.Status.Status().StartAsync(message, async _ => await work().ConfigureAwait(true)).ConfigureAwait(true);
    }

    private static string DefaultFileName() =>
        $"snaply-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png";
}
