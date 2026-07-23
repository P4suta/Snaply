using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Snaply;

public sealed partial class MainPage : Page
{
    // Segoe Fluent Icons glyph code points for the Capture pill, per selected mode.
    private const int RegionGlyph = 0xEF20;
    private const int WindowGlyph = 0xE737;
    private const int DesktopGlyph = 0xE7F4;

    // The mode the Capture pill runs. The flyout only changes this (it never captures on its own);
    // pressing the pill body captures with it. Defaults to the whole desktop.
    private CaptureMode _selectedMode = CaptureMode.Desktop;

    internal MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        UpdatePrimaryCapture();
        ViewModel.PropertyChanged += (_, args) =>
        {
            // Each successful auto-save bumps SavedTick; play the folder→green-check flip.
            if (args.PropertyName == nameof(MainViewModel.SavedTick))
            {
                DispatcherQueue.TryEnqueue(() => SavedFeedback.Begin());
            }
        };
    }

    internal MainViewModel ViewModel { get; }

    // Capture pill body: run the currently selected mode.
    private async void CaptureButton_Click(SplitButton sender, SplitButtonClickEventArgs args) =>
        await ViewModel.CaptureAsync(_selectedMode);

    // Flyout items: change the selected mode only (the capture happens on the pill body click).
    private void RegionCaptureItem_Click(object sender, RoutedEventArgs args) => SelectMode(CaptureMode.Region);

    private void WindowCaptureItem_Click(object sender, RoutedEventArgs args) => SelectMode(CaptureMode.Window);

    private void DesktopCaptureItem_Click(object sender, RoutedEventArgs args) => SelectMode(CaptureMode.Desktop);

    private void OpenFolderButton_Click(object sender, RoutedEventArgs args) => ViewModel.OpenFolder();

    private void SelectMode(CaptureMode mode)
    {
        _selectedMode = mode;
        UpdatePrimaryCapture();
    }

    // Reflect the selected mode on the Capture pill (label + glyph). Kept in code-behind so the
    // view model stays free of presentation strings.
    private void UpdatePrimaryCapture()
    {
        PrimaryCaptureLabel.Text = ResourceText.Get(_selectedMode switch
        {
            CaptureMode.Region => "CaptureRegion",
            CaptureMode.Window => "CaptureWindow",
            _ => "CaptureDesktop",
        });
        PrimaryCaptureGlyph.Glyph = char.ConvertFromUtf32(_selectedMode switch
        {
            CaptureMode.Region => RegionGlyph,
            CaptureMode.Window => WindowGlyph,
            _ => DesktopGlyph,
        });
    }
}
