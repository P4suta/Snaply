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
    }

    /// <summary>Set by the View: shows the region overlay and yields the picked region.</summary>
    public Func<Task<PhysicalRect?>>? RegionSelector { get; set; }

    /// <summary>Set by the View: shows the window picker and yields the chosen window handle (HWND), or null on cancel.</summary>
    public Func<Task<nint?>>? WindowSelector { get; set; }

    // --- Preview ------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(HasNoImage))]
    public partial CapturedImage? BeautifiedImage { get; set; }

    /// <summary>True when a beautified image is available (drives Save/Copy and the preview).</summary>
    public bool HasImage => BeautifiedImage is not null;

    /// <summary>True when there is no image yet (drives the empty-state hint).</summary>
    public bool HasNoImage => BeautifiedImage is null;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether the error InfoBar is visible (two-way so the user can dismiss it).</summary>
    [ObservableProperty]
    public partial bool IsErrorOpen { get; set; }

    /// <summary>The friendly, localized error title shown in the InfoBar.</summary>
    [ObservableProperty]
    public partial string ErrorTitle { get; set; } = string.Empty;

    /// <summary>The raw technical detail (code, message, exception) shown in the InfoBar's details expander.</summary>
    [ObservableProperty]
    public partial string ErrorDetail { get; set; } = string.Empty;

    /// <summary>Increments on every successful auto-save; the View plays a brief "saved" animation on change.</summary>
    [ObservableProperty]
    public partial int SavedTick { get; set; }

    /// <summary>The full path of the most recently auto-saved capture (drives the "open folder" action).</summary>
    [ObservableProperty]
    public partial string LastSavedPath { get; set; } = string.Empty;

    // --- Capture mode (drives the unified Capture SplitButton) ---------------

    /// <summary>The most recently used capture mode; the primary Capture button repeats it.
    /// Defaults to full screen — the most likely first capture.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryCaptureLabel))]
    [NotifyPropertyChangedFor(nameof(PrimaryCaptureGlyph))]
    public partial CaptureMode LastCaptureMode { get; set; } = CaptureMode.FullScreen;

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

    // The SplitButton dropdowns only CHANGE the primary action (they do not fire it).
    // Executing happens when the user clicks the main button. This avoids capturing /
    // exporting by merely picking a mode from the menu.
    [RelayCommand]
    private void SelectCaptureRegion() => LastCaptureMode = CaptureMode.Region;

    [RelayCommand]
    private void SelectCaptureFullScreen() => LastCaptureMode = CaptureMode.FullScreen;

    [RelayCommand]
    private void SelectCaptureWindow() => LastCaptureMode = CaptureMode.Window;

    // Snaply keeps itself out of the shot via SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE),
    // applied once by the View — deterministic, no hide/timing needed here.
    [RelayCommand]
    private async Task CaptureFullScreenAsync()
    {
        LastCaptureMode = CaptureMode.FullScreen;
        IsBusy = true;
        try
        {
            await CaptureAndPersistAsync(_pipeline.CaptureFullScreenAsync(BuildSpec()));
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
            return;
        }

        PhysicalRect? region = await RegionSelector();
        if (region is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await CaptureAndPersistAsync(_pipeline.CaptureRegionAsync(region.Value, BuildSpec()));
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
            return;
        }

        nint? hwnd = await WindowSelector();
        if (hwnd is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await CaptureAndPersistAsync(_pipeline.CaptureWindowAsync(hwnd.Value, BuildSpec()));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Internals ----------------------------------------------------------
    // Snaply always beautifies (that is the whole point): an image-derived background plus the
    // pipeline's size-derived padding / corner radius / shadow (see BeautifyDefaults).
    private static BeautifySpec BuildSpec() =>
        BeautifySpec.Default with { Background = new Background.Auto() };

    // Capture succeeded -> show it, then auto-save to Snaply's own folder and auto-copy to the
    // clipboard. Snaply has no explicit Save/Copy step; the "saved" InfoBar is the only feedback.
    private async Task CaptureAndPersistAsync(Task<Result<CapturedImage>> capture)
    {
        Result<CapturedImage> result = await capture;
        Apply(result);
        if (result.IsSuccess)
        {
            await PersistAsync(result.Value);
        }
    }

    // Auto-save the finished capture and copy it to the clipboard, then raise the "saved" InfoBar
    // (which offers "open folder"). Snaply is portable: captures land in a folder beside the app
    // itself (AppPaths), never in the user's personal environment — no %LOCALAPPDATA% / Pictures pollution.
    private async Task PersistAsync(CapturedImage image)
    {
        string path = Path.Combine(
            AppPaths.Ensure(AppPaths.CapturesDirectory),
            $"Snaply_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        Result<string> saved = await _export.SavePngAsync(image, path);
        if (!saved.IsSuccess)
        {
            ReportError(saved.Error);
            return;
        }

        Result copied = await _export.CopyToClipboardAsync(image);
        if (!copied.IsSuccess)
        {
            ReportError(copied.Error);
            return;
        }

        LastSavedPath = saved.Value;
        SavedTick++;
    }

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
        IsErrorOpen = true;
    }
}
