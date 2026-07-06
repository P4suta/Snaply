namespace Snaply.Core;

/// <summary>
/// The stable, machine-readable failure codes carried by <see cref="Error.Code"/>. Centralised
/// so producers (Platform adapters), the observability layer, and the localized-message mapping
/// all reference the same constants (naming convention: <c>area.reason</c>).
/// </summary>
public static class ErrorCodes
{
    /// <summary>Monitor capture failed (index out of range or capture error).</summary>
    public const string CaptureMonitor = "capture.monitor";

    /// <summary>Window capture failed (invalid handle or capture error).</summary>
    public const string CaptureWindow = "capture.window";

    /// <summary>A window selector (title/process) matched more than one window; caller must disambiguate.</summary>
    public const string CaptureWindowAmbiguous = "capture.window.ambiguous";

    /// <summary>Region capture failed (invalid region, no monitor, or capture error).</summary>
    public const string CaptureRegion = "capture.region";

    /// <summary>Beautify rendering failed.</summary>
    public const string BeautifyRender = "beautify.render";

    /// <summary>Saving the image to disk failed.</summary>
    public const string ExportSave = "export.save";

    /// <summary>Copying the image to the clipboard failed.</summary>
    public const string ExportClipboard = "export.clipboard";

    /// <summary>Registering a global hotkey failed (already in use).</summary>
    public const string HotkeyRegister = "hotkey.register";

    /// <summary>A hotkey chord string could not be parsed.</summary>
    public const string HotkeyParse = "hotkey.parse";

    /// <summary>The hotkey service was used after being disposed.</summary>
    public const string HotkeyDisposed = "hotkey.disposed";

    /// <summary>A re-render was requested before anything had been captured.</summary>
    public const string PipelineNoCapture = "pipeline.nocapture";

    /// <summary>A command-line / tool argument could not be parsed or was out of range.</summary>
    public const string InputInvalid = "input.invalid";

    /// <summary>A capture was requested but consent for it was not granted (MCP capture scope).</summary>
    public const string ConsentDenied = "consent.denied";

    /// <summary>An output was required (e.g. a file path in machine mode) but none was supplied.</summary>
    public const string OutputMissing = "output.missing";
}
