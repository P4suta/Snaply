using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Snaply;

public sealed partial class MainPage : Page
{
    // Segoe Fluent Icons glyph code points for the Capture pill, per selected mode.
    private const int RegionGlyph = 0xEF20;
    private const int WindowGlyph = 0xE737;
    private const int DesktopGlyph = 0xE7F4;

    internal MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        UpdatePrimaryCapture();

        // The Open Folder button is icon-only, so give it an accessible name and a tooltip.
        // Kept in code-behind alongside the other presentation strings (the view model stays
        // free of UI text).
        string openFolder = ResourceText.Get("OpenFolderLabel");
        AutomationProperties.SetName(OpenFolderButton, openFolder);
        ToolTipService.SetToolTip(OpenFolderButton, openFolder);
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

    // Flyout items: change the selected mode only. The pill body is bound to CaptureCommand,
    // which runs whichever mode is selected.
    private void RegionCaptureItem_Click(object sender, RoutedEventArgs args) => SelectMode(CaptureMode.Region);

    private void WindowCaptureItem_Click(object sender, RoutedEventArgs args) => SelectMode(CaptureMode.Window);

    private void DesktopCaptureItem_Click(object sender, RoutedEventArgs args) => SelectMode(CaptureMode.Desktop);

    private void OpenFolderButton_Click(object sender, RoutedEventArgs args) => ViewModel.OpenFolder();

    private void SelectMode(CaptureMode mode)
    {
        ViewModel.SelectedMode = mode;
        UpdatePrimaryCapture();
    }

    // Reflect the selected mode on the Capture pill (label + glyph). Kept in code-behind so the
    // view model stays free of presentation strings.
    private void UpdatePrimaryCapture()
    {
        string label = ResourceText.Get(ViewModel.SelectedMode switch
        {
            CaptureMode.Region => "CaptureRegion",
            CaptureMode.Window => "CaptureWindow",
            _ => "CaptureDesktop",
        });
        PrimaryCaptureLabel.Text = label;
        // The pill's content is a panel, so it derives no automation name of its own and
        // screen readers announce it unnamed. Name it after the mode it will run.
        AutomationProperties.SetName(CaptureButton, label);
        PrimaryCaptureGlyph.Glyph = char.ConvertFromUtf32(ViewModel.SelectedMode switch
        {
            CaptureMode.Region => RegionGlyph,
            CaptureMode.Window => WindowGlyph,
            _ => DesktopGlyph,
        });
    }
}
