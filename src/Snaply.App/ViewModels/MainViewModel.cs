using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Snaply.Diagnostics;
using Snaply.Services;

namespace Snaply.ViewModels;

/// <summary>A named background choice offered in the Settings dialog.</summary>
/// <param name="Name">The display name shown in the picker.</param>
/// <param name="Value">The background this preset applies.</param>
public sealed record BackgroundPreset(string Name, Background Value);

/// <summary>The kind of capture the primary Capture button repeats (its last-used mode).</summary>
public enum CaptureMode
{
    /// <summary>Capture a user-selected region.</summary>
    Region,

    /// <summary>Capture the full screen.</summary>
    FullScreen,

    /// <summary>Capture a chosen window.</summary>
    Window,
}

/// <summary>The export the primary Save/Copy button repeats (its last-used action).</summary>
public enum ExportAction
{
    /// <summary>Save the beautified image to a PNG file.</summary>
    Save,

    /// <summary>Copy the beautified image to the clipboard.</summary>
    Copy,
}

/// <summary>
/// Drives the main window: capture commands, the live beautify controls, and export.
/// It speaks only to the <see cref="CapturePipeline"/> and Core ports/models — no
/// WinUI type appears here. The View adapts the resulting <see cref="CapturedImage"/>
/// to an image source (see <see cref="ImageBridge"/>) and supplies the region/save
/// pickers through the delegate hooks below.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "MainViewModel is a DI-container singleton that lives for the whole app; _renderGate is a SemaphoreSlim used only to coalesce re-renders and never allocates a wait handle, so it needs no deterministic disposal, and adding IDisposable would change the view model's public surface.")]
public partial class MainViewModel : ObservableObject
{
    private readonly CapturePipeline _pipeline;
    private readonly IExportService _export;
    private readonly IUiStrings _strings;
    private readonly ErrorPresenter _errorPresenter;
    private readonly ILogger<MainViewModel> _logger;

    // Coalesces the flurry of re-render requests a slider drag produces: only the
    // newest requested version actually renders.
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private int _renderVersion;

    /// <summary>Creates the view model over the capture pipeline and export service.</summary>
    /// <param name="pipeline">The capture + beautify orchestration.</param>
    /// <param name="export">The save / clipboard export service.</param>
    /// <param name="strings">The localized UI string resolver (keeps WinUI resources out of this VM).</param>
    /// <param name="errorPresenter">Maps failures to localized, user-friendly messages.</param>
    /// <param name="logger">Structured logger for user-action failures.</param>
    public MainViewModel(
        CapturePipeline pipeline,
        IExportService export,
        IUiStrings strings,
        ErrorPresenter errorPresenter,
        ILogger<MainViewModel> logger)
    {
        _pipeline = pipeline;
        _export = export;
        _strings = strings;
        _errorPresenter = errorPresenter;
        _logger = logger;

        StatusMessage = strings.Get("StatusReady");

        BackgroundPresets =
        [
            new BackgroundPreset(strings.Get("BackgroundAuto"), new Background.Auto()),
            new BackgroundPreset(strings.Get("BackgroundNone"), new Background.Solid(Rgba.Transparent)),
            new BackgroundPreset(strings.Get("BackgroundIndigoViolet"), Background.DefaultGradient),
            new BackgroundPreset(strings.Get("BackgroundOcean"), new Background.LinearGradient(new Rgba(14, 165, 233, 255), new Rgba(37, 99, 235, 255), 135)),
            new BackgroundPreset(strings.Get("BackgroundSunset"), new Background.LinearGradient(new Rgba(249, 168, 38, 255), new Rgba(239, 68, 68, 255), 135)),
            new BackgroundPreset(strings.Get("BackgroundWhite"), new Background.Solid(Rgba.White)),
            new BackgroundPreset(strings.Get("BackgroundCharcoal"), new Background.Solid(new Rgba(30, 30, 38, 255))),
        ];
    }

    /// <summary>Set by the View: shows the region overlay and yields the picked region.</summary>
    public Func<Task<PhysicalRect?>>? RegionSelector { get; set; }

    /// <summary>Set by the View: shows the window picker and yields the chosen window handle (HWND), or null on cancel.</summary>
    public Func<Task<nint?>>? WindowSelector { get; set; }

    /// <summary>Set by the View: shows a save dialog and yields the chosen file path.</summary>
    public Func<Task<string?>>? SavePathPicker { get; set; }

    // --- Preview + status ---------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(HasNoImage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryExportCommand))]
    public partial CapturedImage? BeautifiedImage { get; set; }

    /// <summary>True when a beautified image is available (drives Save/Copy and the preview).</summary>
    public bool HasImage => BeautifiedImage is not null;

    /// <summary>True when there is no image yet (drives the empty-state hint).</summary>
    public bool HasNoImage => BeautifiedImage is null;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>Whether the error InfoBar is visible (two-way so the user can dismiss it).</summary>
    [ObservableProperty]
    public partial bool IsErrorOpen { get; set; }

    /// <summary>The friendly, localized error title shown in the InfoBar.</summary>
    [ObservableProperty]
    public partial string ErrorTitle { get; set; } = string.Empty;

    /// <summary>The raw technical detail (code, message, exception) shown in the InfoBar's details expander.</summary>
    [ObservableProperty]
    public partial string ErrorDetail { get; set; } = string.Empty;

    // --- Capture mode (drives the unified Capture SplitButton) ---------------

    /// <summary>The most recently used capture mode; the primary Capture button repeats it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryCaptureLabel))]
    [NotifyPropertyChangedFor(nameof(PrimaryCaptureGlyph))]
    public partial CaptureMode LastCaptureMode { get; set; } = CaptureMode.Region;

    /// <summary>Localized label for the primary Capture button (reflects <see cref="LastCaptureMode"/>).</summary>
    public string PrimaryCaptureLabel => _strings.Get(LastCaptureMode switch
    {
        CaptureMode.FullScreen => "CaptureModeFullScreen",
        CaptureMode.Window => "CaptureModeWindow",
        _ => "CaptureModeRegion",
    });

    /// <summary>Segoe Fluent icon glyph for the primary Capture button (reflects <see cref="LastCaptureMode"/>).</summary>
    public string PrimaryCaptureGlyph => LastCaptureMode switch
    {
        CaptureMode.FullScreen => "",
        CaptureMode.Window => "",
        _ => "",
    };

    /// <summary>The most recently used export action; the primary Save/Copy button repeats it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryExportLabel))]
    [NotifyPropertyChangedFor(nameof(PrimaryExportGlyph))]
    public partial ExportAction LastExportAction { get; set; } = ExportAction.Save;

    /// <summary>Localized label for the primary export button (reflects <see cref="LastExportAction"/>).</summary>
    public string PrimaryExportLabel => _strings.Get(LastExportAction == ExportAction.Copy ? "ExportCopy" : "ExportSave");

    /// <summary>Segoe Fluent icon glyph for the primary export button (reflects <see cref="LastExportAction"/>).</summary>
    public string PrimaryExportGlyph => LastExportAction == ExportAction.Copy ? "" : "";

    // --- Beautify controls (bound to the spec) ------------------------------

    /// <summary>The background choices shown in the Settings dialog.</summary>
    public IReadOnlyList<BackgroundPreset> BackgroundPresets { get; }

    [ObservableProperty]
    public partial int SelectedBackgroundIndex { get; set; } // 0 == Auto (image-derived gradient)

    [ObservableProperty]
    public partial int SelectedAspectIndex { get; set; } // 0 == Auto, matches AspectPreset order

    [ObservableProperty]
    public partial bool ShadowEnabled { get; set; } = true;

    /// <summary>When true (default), Snaply hides its own window during a capture so it isn't in the shot.</summary>
    [ObservableProperty]
    public partial bool HideOnCapture { get; set; } = true;

    partial void OnSelectedBackgroundIndexChanged(int value) => ScheduleRerender();

    partial void OnSelectedAspectIndexChanged(int value) => ScheduleRerender();

    partial void OnShadowEnabledChanged(bool value) => ScheduleRerender();

    // --- Commands -----------------------------------------------------------

    /// <summary>Repeats the last-used capture mode (the primary action of the Capture button).</summary>
    /// <returns>A task that completes when the capture finishes.</returns>
    [RelayCommand]
    private Task PrimaryCaptureAsync() => LastCaptureMode switch
    {
        CaptureMode.FullScreen => CaptureFullScreenAsync(),
        CaptureMode.Window => CaptureWindowAsync(),
        _ => CaptureRegionAsync(),
    };

    /// <summary>Repeats the last-used export action (the primary action of the Save/Copy button).</summary>
    /// <returns>A task that completes when the export finishes.</returns>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private Task PrimaryExportAsync() =>
        LastExportAction == ExportAction.Copy ? CopyAsync() : SaveAsync();

    // The SplitButton dropdowns only CHANGE the primary action (they do not fire it).
    // Executing happens when the user clicks the main button. This avoids capturing /
    // exporting by merely picking a mode from the menu.
    [RelayCommand]
    private void SelectCaptureRegion() => LastCaptureMode = CaptureMode.Region;

    [RelayCommand]
    private void SelectCaptureFullScreen() => LastCaptureMode = CaptureMode.FullScreen;

    [RelayCommand]
    private void SelectCaptureWindow() => LastCaptureMode = CaptureMode.Window;

    [RelayCommand]
    private void SelectExportSave() => LastExportAction = ExportAction.Save;

    [RelayCommand]
    private void SelectExportCopy() => LastExportAction = ExportAction.Copy;

    // Snaply keeps itself out of the shot via SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE),
    // applied by the View from HideOnCapture — deterministic, no hide/timing needed here.
    [RelayCommand]
    private async Task CaptureFullScreenAsync()
    {
        LastCaptureMode = CaptureMode.FullScreen;
        IsBusy = true;
        StatusMessage = _strings.Get("StatusCapturingFullScreen");
        try
        {
            Apply(await _pipeline.CaptureFullScreenAsync(BuildSpec()));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CaptureRegionAsync()
    {
        LastCaptureMode = CaptureMode.Region;
        if (RegionSelector is null)
        {
            StatusMessage = _strings.Get("StatusRegionUnavailable");
            return;
        }

        StatusMessage = _strings.Get("StatusSelectRegion");
        PhysicalRect? region = await RegionSelector();
        if (region is null)
        {
            StatusMessage = _strings.Get("StatusRegionCancelled");
            return;
        }

        IsBusy = true;
        StatusMessage = _strings.Get("StatusCapturingRegion");
        try
        {
            Apply(await _pipeline.CaptureRegionAsync(region.Value, BuildSpec()));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CaptureWindowAsync()
    {
        LastCaptureMode = CaptureMode.Window;
        if (WindowSelector is null)
        {
            StatusMessage = _strings.Get("StatusWindowUnavailable");
            return;
        }

        StatusMessage = _strings.Get("StatusSelectWindow");
        nint? hwnd = await WindowSelector();
        if (hwnd is null)
        {
            StatusMessage = _strings.Get("StatusWindowCancelled");
            return;
        }

        IsBusy = true;
        StatusMessage = _strings.Get("StatusCapturingWindow");
        try
        {
            Apply(await _pipeline.CaptureWindowAsync(hwnd.Value, BuildSpec()));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task SaveAsync()
    {
        LastExportAction = ExportAction.Save;
        if (BeautifiedImage is null || SavePathPicker is null)
        {
            return;
        }

        string? path = await SavePathPicker();
        if (path is null)
        {
            StatusMessage = _strings.Get("StatusSaveCancelled");
            return;
        }

        Result<string> result = await _export.SavePngAsync(BeautifiedImage, path);
        if (result.IsSuccess)
        {
            StatusMessage = _strings.Format("StatusSavedFormat", result.Value);
        }
        else
        {
            ReportError(result.Error);
        }
    }

    // Clipboard write must run on the UI thread; RelayCommand invokes on the
    // DispatcherQueue thread, so this satisfies IExportService's contract.
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task CopyAsync()
    {
        LastExportAction = ExportAction.Copy;
        if (BeautifiedImage is null)
        {
            return;
        }

        Result result = await _export.CopyToClipboardAsync(BeautifiedImage);
        if (result.IsSuccess)
        {
            StatusMessage = _strings.Get("StatusCopied");
        }
        else
        {
            ReportError(result.Error);
        }
    }

    // --- Internals ----------------------------------------------------------
    // Padding and corner radius are intentionally omitted: the CapturePipeline derives them
    // automatically from the raw capture size (see BeautifyDefaults) for an aesthetic result.
    private BeautifySpec BuildSpec() => BeautifySpec.Default with
    {
        Background = BackgroundPresets[Math.Clamp(SelectedBackgroundIndex, 0, BackgroundPresets.Count - 1)].Value,
        Aspect = (AspectPreset)Math.Clamp(SelectedAspectIndex, 0, 3),
        Shadow = ShadowEnabled ? ShadowSpec.Default : ShadowSpec.None,
    };

    private async void ScheduleRerender()
    {
        if (!_pipeline.HasCapture)
        {
            return;
        }

        int version = Interlocked.Increment(ref _renderVersion);

        // async void: an escaped exception would crash the process, so guard the whole body.
        try
        {
            await _renderGate.WaitAsync();
            try
            {
                if (version != Volatile.Read(ref _renderVersion))
                {
                    return; // a newer edit superseded this one while we waited for the gate
                }

                IsBusy = true;
                Result<CapturedImage> result = await _pipeline.RerenderAsync(BuildSpec());
                if (version == Volatile.Read(ref _renderVersion))
                {
                    Apply(result);
                }
            }
            finally
            {
                if (version == Volatile.Read(ref _renderVersion))
                {
                    IsBusy = false;
                }

                _renderGate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.BackgroundOperationFailed(_logger, "Rerender", ex);
        }
    }

    private void Apply(Result<CapturedImage> result)
    {
        if (result.IsSuccess)
        {
            BeautifiedImage = result.Value;
            StatusMessage = _strings.Format("StatusDimensionsFormat", result.Value.Size.Width, result.Value.Size.Height);
            IsErrorOpen = false;
        }
        else
        {
            ReportError(result.Error);
        }
    }

    // Log the failure with full context and surface a localized, friendly message; the raw
    // technical detail (including any exception) stays available in the InfoBar's details expander.
    private void ReportError(Error error)
    {
        AppLog.UserActionFailed(_logger, error.Code, error.Message, error.Cause);

        PresentedError presented = _errorPresenter.Present(error);
        ErrorTitle = presented.Title;
        ErrorDetail = presented.Detail;
        StatusMessage = presented.Title;
        IsErrorOpen = true;
    }
}
