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
    private readonly WindowResolver _resolver;
    private readonly CapturePolicy _policy;

    /// <summary>Creates the tool set with the shared use cases injected from DI.</summary>
    /// <param name="pipeline">The capture + beautify pipeline.</param>
    /// <param name="capture">The raw screen capture service.</param>
    /// <param name="export">The image export service (PNG encode / save).</param>
    /// <param name="windows">The window enumerator.</param>
    /// <param name="monitors">The monitor enumerator.</param>
    /// <param name="resolver">The shared window resolver (targeting + group regions).</param>
    /// <param name="policy">The capture consent policy.</param>
    public SnaplyMcpTools(
        CapturePipeline pipeline,
        IScreenCaptureService capture,
        IExportService export,
        IWindowEnumerationService windows,
        IMonitorEnumerationService monitors,
        WindowResolver resolver,
        CapturePolicy policy)
    {
        _pipeline = pipeline;
        _capture = capture;
        _export = export;
        _windows = windows;
        _monitors = monitors;
        _resolver = resolver;
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
    [Description("List capturable top-level windows (front to back). Pass a returned 'handle' to capture_window for an exact target; 'process'/'title' may match several. 'foreground':true marks the active window.")]
    public CallToolResult ListWindows()
    {
        object data = _windows.EnumerateTopLevelWindows().Select(w => new
        {
            handle = ToHex(w.Handle),
            title = w.Title,
            processName = w.ProcessName,
            processId = w.ProcessId,
            className = w.ClassName,
            owner = w.OwnerHandle == 0 ? null : ToHex(w.OwnerHandle),
            foreground = w.IsForeground,
            bounds = new { x = w.Bounds.X, y = w.Bounds.Y, width = w.Bounds.Width, height = w.Bounds.Height },
        }).ToArray();
        return Json(data);
    }

    [McpServerTool(Name = "capture_fullscreen")]
    [Description("Capture a full monitor. Returns the raw PNG by default (best for reading UI); pass beautify:true for the styled look. Returns the image, or saves it when output='file'. Requires capture consent.")]
    public Task<CallToolResult> CaptureFullscreen(
        [Description("Monitor index (0 = primary).")] int monitor = 0,
        [Description("Apply the automatic beautify styling (padding/background/shadow). Off by default.")] bool beautify = false,
        [Description("auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@135 | image:<path>")] string? background = null,
        [Description("Padding: N or L,T,R,B (physical px).")] string? padding = null,
        [Description("Corner radius in physical px.")] double? cornerRadius = null,
        [Description("none | default | offX,offY,blur,opacity[,#RRGGBB]")] string? shadow = null,
        [Description("auto | square | standard | wide")] string? aspect = null,
        [Description("'image' returns the PNG; 'file' saves it to 'path'.")] string output = "image",
        [Description("Destination path when output='file'.")] string? path = null,
        [Description("Wait this many milliseconds before capturing (let UI settle first).")] int delayMs = 0,
        [Description("Must be true when the server runs in prompt-once consent mode.")] bool confirmed = false,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(new CaptureTarget.Monitor(monitor), beautify, background, padding, cornerRadius, shadow, aspect, output, path, delayMs, confirmed, cancellationToken);

    [McpServerTool(Name = "capture_region")]
    [Description("Capture a rectangular region of the virtual desktop (physical pixels). Returns the raw PNG by default; pass beautify:true for the styled look. Requires capture consent.")]
    public Task<CallToolResult> CaptureRegion(
        [Description("Left edge (physical px).")] int x,
        [Description("Top edge (physical px).")] int y,
        [Description("Width (physical px, > 0).")] int width,
        [Description("Height (physical px, > 0).")] int height,
        [Description("Apply the automatic beautify styling (padding/background/shadow). Off by default.")] bool beautify = false,
        [Description("auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@135 | image:<path>")] string? background = null,
        [Description("Padding: N or L,T,R,B (physical px).")] string? padding = null,
        [Description("Corner radius in physical px.")] double? cornerRadius = null,
        [Description("none | default | offX,offY,blur,opacity[,#RRGGBB]")] string? shadow = null,
        [Description("auto | square | standard | wide")] string? aspect = null,
        [Description("'image' returns the PNG; 'file' saves it to 'path'.")] string output = "image",
        [Description("Destination path when output='file'.")] string? path = null,
        [Description("Wait this many milliseconds before capturing (let UI settle first).")] int delayMs = 0,
        [Description("Must be true when the server runs in prompt-once consent mode.")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0)
        {
            return Task.FromResult(Error(ErrorCodes.InputInvalid, "Region width and height must be positive."));
        }

        return CaptureAsync(new CaptureTarget.Region(new PhysicalRect(x, y, width, height)), beautify, background, padding, cornerRadius, shadow, aspect, output, path, delayMs, confirmed, cancellationToken);
    }

    [McpServerTool(Name = "capture_window")]
    [Description("Capture a top-level window. Target it by 'handle' (exact, from list_windows), 'title' substring, or 'process' name; with none given the active/foreground window is used. Set includePopups:true to also grab its file picker / dialog / menu. Returns the raw PNG by default. If a title/process matches several windows the call fails with a 'candidates' list — retry with a handle. Requires capture consent.")]
    public Task<CallToolResult> CaptureWindow(
        [Description("Window handle, e.g. '0x00A2' from list_windows (exact target).")] string? handle = null,
        [Description("Match windows whose title contains this text.")] string? title = null,
        [Description("Match windows owned by this process (name, '.exe' optional).")] string? process = null,
        [Description("Capture the foreground window (also the default when no target is given).")] bool active = false,
        [Description("Also capture the window's owned dialogs/popups (file picker, menus) as one image.")] bool includePopups = false,
        [Description("Apply the automatic beautify styling (padding/background/shadow). Off by default.")] bool beautify = false,
        [Description("auto | solid:#RRGGBB | gradient:#RRGGBB,#RRGGBB@135 | image:<path>")] string? background = null,
        [Description("Padding: N or L,T,R,B (physical px).")] string? padding = null,
        [Description("Corner radius in physical px.")] double? cornerRadius = null,
        [Description("none | default | offX,offY,blur,opacity[,#RRGGBB]")] string? shadow = null,
        [Description("auto | square | standard | wide")] string? aspect = null,
        [Description("'image' returns the PNG; 'file' saves it to 'path'.")] string output = "image",
        [Description("Destination path when output='file'.")] string? path = null,
        [Description("Wait this many milliseconds before capturing (open a menu/dialog first).")] int delayMs = 0,
        [Description("Must be true when the server runs in prompt-once consent mode.")] bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        Result<nint> parsedHandle = CaptureArguments.ParseWindowHandle(handle, allowEmpty: true);
        if (parsedHandle.IsFailure)
        {
            return Task.FromResult(Error(parsedHandle.Error.Code, parsedHandle.Error.Message));
        }

        WindowResolution resolution = _resolver.Resolve(new WindowQuery(
            Handle: string.IsNullOrWhiteSpace(handle) ? null : parsedHandle.Value,
            TitleContains: title,
            ProcessName: process,
            Active: active));

        switch (resolution)
        {
            case WindowResolution.Ambiguous ambiguous:
                return Task.FromResult(AmbiguousError(ambiguous.Candidates));

            case WindowResolution.NotFound notFound:
                return Task.FromResult(Error(notFound.Error.Code, notFound.Error.Message));

            case WindowResolution.Resolved resolved:
                if (!includePopups)
                {
                    return CaptureAsync(new CaptureTarget.Window(resolved.Window.Handle), beautify, background, padding, cornerRadius, shadow, aspect, output, path, delayMs, confirmed, cancellationToken);
                }

                Result<PhysicalRect> group = _resolver.ResolveGroupRegion(resolved.Window.Handle);
                return group.IsFailure
                    ? Task.FromResult(Error(group.Error.Code, group.Error.Message))
                    : CaptureAsync(new CaptureTarget.Region(group.Value), beautify, background, padding, cornerRadius, shadow, aspect, output, path, delayMs, confirmed, cancellationToken);

            default:
                return Task.FromResult(Error(ErrorCodes.CaptureWindow, "Could not resolve a window."));
        }
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
        int delayMs,
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

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
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

    // A structured ambiguity error: the candidate windows are returned so the AI can retry with an
    // exact handle instead of a title/process selector that matched several windows.
    private static CallToolResult AmbiguousError(IReadOnlyList<WindowInfo> candidates)
    {
        var payload = new
        {
            code = ErrorCodes.CaptureWindowAmbiguous,
            message = $"{candidates.Count} windows matched. Retry capture_window with one of these 'handle' values.",
            candidates = candidates.Select(w => new
            {
                handle = ToHex(w.Handle),
                title = w.Title,
                processName = w.ProcessName,
                bounds = new { x = w.Bounds.X, y = w.Bounds.Y, width = w.Bounds.Width, height = w.Bounds.Height },
            }).ToArray(),
        };
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(payload, JsonOptions),
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }],
        };
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
