using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snaply.Application;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Cli.Mcp;

/// <summary>
/// The Model Context Protocol tools Snaply exposes to AI clients. Read-only listing tools
/// (<c>list_monitors</c> / <c>list_windows</c>) are always available; the capture tools are
/// gated by the <see cref="CapturePolicy"/> so an AI cannot silently grab the screen. Every
/// capture tool wraps the same shared use cases the CLI uses, so behaviour is identical.
/// </summary>
[McpServerToolType]
[SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the MCP server via reflection (WithTools<T>).")]
internal sealed class SnaplyMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly CapturePipeline _pipeline;
    private readonly IScreenCaptureService _capture;
    private readonly IExportService _export;
    private readonly IWindowEnumerationService _windows;
    private readonly IMonitorEnumerationService _monitors;
    private readonly CapturePolicy _policy;

    /// <summary>Creates the tool set with the shared use cases injected from DI.</summary>
    /// <param name="pipeline">The capture + beautify pipeline.</param>
    /// <param name="capture">The raw screen capture service.</param>
    /// <param name="export">The image export service (PNG encode / save).</param>
    /// <param name="windows">The window enumerator.</param>
    /// <param name="monitors">The monitor enumerator.</param>
    /// <param name="policy">The capture consent policy.</param>
    public SnaplyMcpTools(
        CapturePipeline pipeline,
        IScreenCaptureService capture,
        IExportService export,
        IWindowEnumerationService windows,
        IMonitorEnumerationService monitors,
        CapturePolicy policy)
    {
        _pipeline = pipeline;
        _capture = capture;
        _export = export;
        _windows = windows;
        _monitors = monitors;
        _policy = policy;
    }

    [McpServerTool(Name = "list_monitors")]
    [Description("List the connected display monitors. The 'index' matches capture_fullscreen's monitor argument.")]
    public CallToolResult ListMonitors()
    {
        object data = _monitors.EnumerateMonitors().Select(m => new
        {
            index = m.Index,
            primary = m.Primary,
            dpi = m.Dpi.Value,
            bounds = new { x = m.Bounds.X, y = m.Bounds.Y, width = m.Bounds.Width, height = m.Bounds.Height },
        }).ToArray();
        return Json(data);
    }

    [McpServerTool(Name = "list_windows")]
    [Description("List capturable top-level windows (front to back). Pass a returned 'handle' to capture_window.")]
    public CallToolResult ListWindows()
    {
        object data = _windows.EnumerateTopLevelWindows().Select(w => new
        {
            handle = ToHex(w.Handle),
            title = w.Title,
            bounds = new { x = w.Bounds.X, y = w.Bounds.Y, width = w.Bounds.Width, height = w.Bounds.Height },
        }).ToArray();
        return Json(data);
    }

    [McpServerTool(Name = "capture_fullscreen")]
    [Description("Capture a full monitor and (by default) beautify it. Returns the PNG image, or saves it when output='file'. Requires capture consent.")]
    public Task<CallToolResult> CaptureFullscreen(
        [Description("Monitor index (0 = primary).")] int monitor = 0,
        [Description("Apply the automatic beautify styling.")] bool beautify = true,
        [Description("auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@135 | image:<path>")] string? background = null,
        [Description("Padding: N or L,T,R,B (physical px).")] string? padding = null,
        [Description("Corner radius in physical px.")] double? cornerRadius = null,
        [Description("none | default | offX,offY,blur,opacity[,#RRGGBB]")] string? shadow = null,
        [Description("auto | square | standard | wide")] string? aspect = null,
        [Description("'image' returns the PNG; 'file' saves it to 'path'.")] string output = "image",
        [Description("Destination path when output='file'.")] string? path = null,
        [Description("Must be true when the server runs in prompt-once consent mode.")] bool confirmed = false,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(new CaptureTarget.Monitor(monitor), beautify, background, padding, cornerRadius, shadow, aspect, output, path, confirmed, cancellationToken);

    [McpServerTool(Name = "capture_region")]
    [Description("Capture a rectangular region of the virtual desktop (physical pixels) and (by default) beautify it. Requires capture consent.")]
    public Task<CallToolResult> CaptureRegion(
        [Description("Left edge (physical px).")] int x,
        [Description("Top edge (physical px).")] int y,
        [Description("Width (physical px, > 0).")] int width,
        [Description("Height (physical px, > 0).")] int height,
        [Description("Apply the automatic beautify styling.")] bool beautify = true,
        [Description("auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@135 | image:<path>")] string? background = null,
        [Description("Padding: N or L,T,R,B (physical px).")] string? padding = null,
        [Description("Corner radius in physical px.")] double? cornerRadius = null,
        [Description("none | default | offX,offY,blur,opacity[,#RRGGBB]")] string? shadow = null,
        [Description("auto | square | standard | wide")] string? aspect = null,
        [Description("'image' returns the PNG; 'file' saves it to 'path'.")] string output = "image",
        [Description("Destination path when output='file'.")] string? path = null,
        [Description("Must be true when the server runs in prompt-once consent mode.")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0)
        {
            return Task.FromResult(Error(ErrorCodes.InputInvalid, "Region width and height must be positive."));
        }

        return CaptureAsync(new CaptureTarget.Region(new PhysicalRect(x, y, width, height)), beautify, background, padding, cornerRadius, shadow, aspect, output, path, confirmed, cancellationToken);
    }

    [McpServerTool(Name = "capture_window")]
    [Description("Capture a single top-level window by handle (from list_windows) or title substring, and (by default) beautify it. Requires capture consent.")]
    public Task<CallToolResult> CaptureWindow(
        [Description("Window handle, e.g. '0x00A2' from list_windows.")] string? handle = null,
        [Description("Match the first window whose title contains this text.")] string? title = null,
        [Description("Apply the automatic beautify styling.")] bool beautify = true,
        [Description("auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@135 | image:<path>")] string? background = null,
        [Description("Padding: N or L,T,R,B (physical px).")] string? padding = null,
        [Description("Corner radius in physical px.")] double? cornerRadius = null,
        [Description("none | default | offX,offY,blur,opacity[,#RRGGBB]")] string? shadow = null,
        [Description("auto | square | standard | wide")] string? aspect = null,
        [Description("'image' returns the PNG; 'file' saves it to 'path'.")] string output = "image",
        [Description("Destination path when output='file'.")] string? path = null,
        [Description("Must be true when the server runs in prompt-once consent mode.")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        Result<nint> resolved = ResolveWindow(handle, title);
        return resolved.IsFailure
            ? Task.FromResult(Error(resolved.Error.Code, resolved.Error.Message))
            : CaptureAsync(new CaptureTarget.Window(resolved.Value), beautify, background, padding, cornerRadius, shadow, aspect, output, path, confirmed, cancellationToken);
    }

    private async Task<CallToolResult> CaptureAsync(
        CaptureTarget target,
        bool beautify,
        string? background,
        string? padding,
        double? cornerRadius,
        string? shadow,
        string? aspect,
        string output,
        string? path,
        bool confirmed,
        CancellationToken ct)
    {
        if (!_policy.AllowCapture)
        {
            return Error(ErrorCodes.ConsentDenied, "Screen capture is disabled. Start the server with --allow-capture (and --consent-mode allow|prompt-once).");
        }

        if (_policy.RequireConfirm && !confirmed)
        {
            return Error(ErrorCodes.ConsentDenied, "This server runs in prompt-once consent mode; pass confirmed:true to authorize the capture.");
        }

        bool wantFile = string.Equals(output, "file", StringComparison.OrdinalIgnoreCase);
        if (wantFile && string.IsNullOrWhiteSpace(path))
        {
            return Error(ErrorCodes.OutputMissing, "output='file' requires a 'path'.");
        }

        Result<BeautifySpec?> spec = BeautifySpecMapper.Map(new BeautifyOptions(
            NoBeautify: !beautify, Background: background, Padding: padding, CornerRadius: cornerRadius, Shadow: shadow, Aspect: aspect));
        if (spec.IsFailure)
        {
            return Error(spec.Error.Code, spec.Error.Message);
        }

        Result<CapturedImage> captured = await CaptureImageAsync(target, spec.Value, ct).ConfigureAwait(false);
        if (captured.IsFailure)
        {
            return Error(captured.Error.Code, captured.Error.Message);
        }

        CapturedImage image = captured.Value;

        if (wantFile)
        {
            Result<string> saved = await _export.SavePngAsync(image, path!, ct).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                return Error(saved.Error.Code, saved.Error.Message);
            }

            var fileSummary = new { width = image.Size.Width, height = image.Size.Height, dpi = image.Dpi.Value, beautified = spec.Value is not null, savedPath = saved.Value };
            return Json(fileSummary);
        }

        Result<byte[]> png = await _export.EncodePngAsync(image, ct).ConfigureAwait(false);
        if (png.IsFailure)
        {
            return Error(png.Error.Code, png.Error.Message);
        }

        var summary = new { width = image.Size.Width, height = image.Size.Height, dpi = image.Dpi.Value, beautified = spec.Value is not null, bytes = png.Value.Length };
        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(summary, JsonOptions),
            Content =
            [
                new TextContentBlock { Text = JsonSerializer.Serialize(summary, JsonOptions) },
                ImageContentBlock.FromBytes(png.Value, "image/png"),
            ],
        };
    }

    private async Task<Result<CapturedImage>> CaptureImageAsync(CaptureTarget target, BeautifySpec? spec, CancellationToken ct)
    {
        if (spec is null)
        {
            return target switch
            {
                CaptureTarget.Monitor m => await _capture.CaptureMonitorAsync(m.Index, ct).ConfigureAwait(false),
                CaptureTarget.Region r => await _capture.CaptureRegionAsync(r.Rect, ct).ConfigureAwait(false),
                CaptureTarget.Window w => await _capture.CaptureWindowAsync(w.Handle, ct).ConfigureAwait(false),
                _ => Result<CapturedImage>.Fail(ErrorCodes.InputInvalid, "Unknown capture target."),
            };
        }

        return target switch
        {
            CaptureTarget.Monitor m => await _pipeline.CaptureMonitorAsync(m.Index, spec, ct).ConfigureAwait(false),
            CaptureTarget.Region r => await _pipeline.CaptureRegionAsync(r.Rect, spec, ct).ConfigureAwait(false),
            CaptureTarget.Window w => await _pipeline.CaptureWindowAsync(w.Handle, spec, ct).ConfigureAwait(false),
            _ => Result<CapturedImage>.Fail(ErrorCodes.InputInvalid, "Unknown capture target."),
        };
    }

    private Result<nint> ResolveWindow(string? handle, string? title)
    {
        bool hasHandle = !string.IsNullOrWhiteSpace(handle);
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        if (hasHandle == hasTitle)
        {
            return Result<nint>.Fail(ErrorCodes.InputInvalid, "Specify exactly one of 'handle' or 'title'.");
        }

        if (hasHandle)
        {
            string trimmed = handle!.Trim();
            bool hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(hex ? trimmed[2..] : trimmed, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long value) || value == 0)
            {
                return Result<nint>.Fail(ErrorCodes.InputInvalid, $"Invalid window handle '{handle}'.");
            }

            return Result<nint>.Ok((nint)value);
        }

        WindowInfo? match = _windows.EnumerateTopLevelWindows().FirstOrDefault(w => w.Title.Contains(title!, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? Result<nint>.Fail(ErrorCodes.CaptureWindow, $"No window title contains '{title}'.")
            : Result<nint>.Ok(match.Handle);
    }

    private static CallToolResult Json(object data) => new()
    {
        StructuredContent = JsonSerializer.SerializeToElement(data, JsonOptions),
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(data, JsonOptions) }],
    };

    private static CallToolResult Error(string code, string message)
    {
        var payload = new { code, message };
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(payload, JsonOptions),
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }],
        };
    }

    private static string ToHex(nint handle) => "0x" + handle.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
}
