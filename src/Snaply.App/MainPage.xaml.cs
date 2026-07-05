using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Snaply.Core.Geometry;
using Snaply.Core.Ports;
using Snaply.Diagnostics;
using Snaply.Services;
using Snaply.ViewModels;
using Snaply.Views;
using Windows.ApplicationModel.DataTransfer;

namespace Snaply;

/// <summary>
/// The main content page. Resolves the shared <see cref="MainViewModel"/> from the
/// composition root, adapts the beautified <c>CapturedImage</c> to the preview
/// <see cref="Image"/>, and supplies the region-overlay + save-dialog hooks the
/// ViewModel invokes (keeping WinUI types out of the ViewModel).
/// </summary>
public sealed partial class MainPage : Page
{
    // WDA_EXCLUDEFROMCAPTURE keeps Snaply out of every capture API (WGC included) while it stays
    // visible on the monitor — deterministic, no hiding/timing. Driven by HideOnCapture.
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private readonly ThemeService _theme;
    private readonly IUiStrings _strings;
    private readonly ILogger<MainPage> _logger;

    // Guards against a second Settings dialog opening while one is already showing.
    private bool _isSettingsOpen;

    /// <summary>The shared view model that drives this page (bound via x:Bind).</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>Resolves the view model and theme service and wires up the page.</summary>
    public MainPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _theme = App.Services.GetRequiredService<ThemeService>();
        _strings = App.Services.GetRequiredService<IUiStrings>();
        _logger = App.Services.GetRequiredService<ILogger<MainPage>>();

        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RegionSelector = PickRegionAsync;
        ViewModel.WindowSelector = PickWindowAsync;
        ViewModel.SavePathPicker = PickSavePathAsync;

        Loaded += OnLoaded;
    }

    /// <summary>Bool -> Visibility helper for x:Bind (no IValueConverter).</summary>
    /// <param name="value">The flag to map.</param>
    /// <returns><see cref="Visibility.Visible"/> when true, otherwise <see cref="Visibility.Collapsed"/>.</returns>
    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Apply the persisted theme to the window's root content (covers this page
        // plus the title bar chrome). The root FrameworkElement is the window's
        // content grid, set up in MainWindow.
        if (App.Window.Content is FrameworkElement root)
        {
            _theme.Initialize(root);
        }

        ApplyCaptureExclusion(); // the window handle is valid once loaded
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_isSettingsOpen)
        {
            return;
        }

        _isSettingsOpen = true;
        try
        {
            // The dialog binds to the shared MainViewModel, so tweaks re-render the live
            // preview. XamlRoot must be set before ShowAsync for unpackaged apps.
            var dialog = new SettingsDialog { XamlRoot = XamlRoot };
            await dialog.ShowAsync();

            // A language change only takes full effect after a restart (x:Uid/MRT resolve
            // at load time), so offer one once the settings dialog has closed.
            if (dialog.NeedsRestartForLanguage)
            {
                await PromptLanguageRestartAsync();
            }
        }
        catch (Exception ex)
        {
            // async void: never let a dialog failure escape and crash the process.
            AppLog.BackgroundOperationFailed(_logger, "SettingsDialog", ex);
        }
        finally
        {
            _isSettingsOpen = false;
        }
    }

    private async Task PromptLanguageRestartAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("RestartDialogTitle"),
            Content = _strings.Get("RestartDialogBody"),
            PrimaryButtonText = _strings.Get("RestartNowButton"),
            CloseButtonText = _strings.Get("RestartLaterButton"),
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _ = Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
        }
    }

    private void OnCopyErrorDetailClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(ViewModel.ErrorDetail);
            Clipboard.SetContent(package);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Copying the diagnostic text is a convenience; the detail is still on screen.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainViewModel.BeautifiedImage), StringComparison.Ordinal))
        {
            if (ViewModel.BeautifiedImage is null)
            {
                PreviewImageControl.Source = null;
                DimensionsText.Text = string.Empty;
            }
            else
            {
                PreviewImageControl.Source = ImageBridge.ToWriteableBitmap(ViewModel.BeautifiedImage);
                DimensionsText.Text = _strings.Format(
                    "StatusDimensionsFormat",
                    ViewModel.BeautifiedImage.Size.Width,
                    ViewModel.BeautifiedImage.Size.Height);
            }
        }
        else if (string.Equals(e.PropertyName, nameof(MainViewModel.HideOnCapture), StringComparison.Ordinal))
        {
            ApplyCaptureExclusion();
        }
    }

    private async Task<PhysicalRect?> PickRegionAsync()
    {
        IScreenCaptureService capture = App.Services.GetRequiredService<IScreenCaptureService>();
        var overlay = new RegionOverlayWindow();
        return await overlay.PickRegionAsync(capture);
    }

    private async Task<nint?> PickWindowAsync()
    {
        IWindowEnumerationService enumerator = App.Services.GetRequiredService<IWindowEnumerationService>();
        IScreenCaptureService capture = App.Services.GetRequiredService<IScreenCaptureService>();
        var overlay = new WindowPickerOverlay();
        return await overlay.PickWindowAsync(enumerator, capture);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    private void ApplyCaptureExclusion() =>
        _ = SetWindowDisplayAffinity(App.WindowHandle, ViewModel.HideOnCapture ? WdaExcludeFromCapture : WdaNone);

    private static async Task<string?> PickSavePathAsync()
    {
        IUiStrings strings = App.Services.GetRequiredService<IUiStrings>();
        var picker = new FileSavePicker(App.WindowId)
        {
            SuggestedFileName = $"Snaply_{DateTime.Now:yyyyMMdd_HHmmss}",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeChoices.Add(strings.Get("SaveFileTypePng"), new List<string> { ".png" });

        PickFileResult? result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}
