using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snaply.Core.Geometry;
using Snaply.Core.Ports;
using Snaply.Services;
using Snaply.ViewModels;
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
    // visible on the monitor — deterministic, no hiding/timing. Always on: Snaply never appears
    // in its own shots.
    private const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>The shared view model that drives this page (bound via x:Bind).</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>Resolves the view model and wires up the page.</summary>
    public MainPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();

        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RegionSelector = PickRegionAsync;
        ViewModel.WindowSelector = PickWindowAsync;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        ApplyCaptureExclusion(); // the window handle is valid once loaded

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
            PreviewImageControl.Source = ViewModel.BeautifiedImage is null
                ? null
                : ImageBridge.ToWriteableBitmap(ViewModel.BeautifiedImage);
        }
        else if (string.Equals(e.PropertyName, nameof(MainViewModel.SavedTick), StringComparison.Ordinal))
        {
            // Auto-save just landed: flip the folder icon to a green check and back.
            SavedFeedback.Begin();
        }
    }

    // Reveal the last saved capture in Explorer (file selected), or just open the captures
    // folder if nothing has been saved yet. Best-effort convenience.
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        string path = ViewModel.LastSavedPath;
        string args = string.IsNullOrEmpty(path)
            ? $"\"{AppPaths.Ensure(AppPaths.CapturesDirectory)}\""
            : $"/select,\"{path}\"";

        try
        {
            using (Process.Start("explorer.exe", args))
            {
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Opening Explorer is best-effort; the file is already saved.
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

    private static void ApplyCaptureExclusion() =>
        _ = SetWindowDisplayAffinity(App.WindowHandle, WdaExcludeFromCapture);
}
