using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.Storage.Streams;

namespace Snaply;

internal sealed partial class MainPage : Page, IDisposable
{
    private const int RegionGlyph = 0xEF20;
    private const int WindowGlyph = 0xE737;
    private const int DesktopGlyph = 0xE7F4;
    private int _previewGeneration;
    private bool _disposed;

    internal MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        UpdatePrimaryCapture();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    internal MainViewModel ViewModel { get; }

    internal static Visibility NullToVisibility(object? value) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    internal static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    internal static InfoBarSeverity ToInfoBarSeverity(NoticeKind kind) =>
        kind switch
        {
            NoticeKind.Success => InfoBarSeverity.Success,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _previewGeneration++;
    }

    private void RegionCaptureItem_Click(object sender, RoutedEventArgs args) =>
        SelectMode(CaptureMode.Region);

    private void WindowCaptureItem_Click(object sender, RoutedEventArgs args) =>
        SelectMode(CaptureMode.Window);

    private void DesktopCaptureItem_Click(object sender, RoutedEventArgs args) =>
        SelectMode(CaptureMode.Desktop);

    private void SelectMode(CaptureMode mode)
    {
        ViewModel.SelectedMode = mode;
        UpdatePrimaryCapture();
    }

    private void UpdatePrimaryCapture()
    {
        string label = ResourceText.Get(ViewModel.SelectedMode switch
        {
            CaptureMode.Region => "CaptureRegion",
            CaptureMode.Window => "CaptureWindow",
            _ => "CaptureDesktop",
        });
        PrimaryCaptureLabel.Text = label;
        AutomationProperties.SetName(CaptureButton, label);
        PrimaryCaptureGlyph.Glyph = char.ConvertFromUtf32(ViewModel.SelectedMode switch
        {
            CaptureMode.Region => RegionGlyph,
            CaptureMode.Window => WindowGlyph,
            _ => DesktopGlyph,
        });
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.PreviewImage))
        {
            return;
        }

        int generation = ++_previewGeneration;
        RenderedImage? image = ViewModel.PreviewImage;
        if (image is null)
        {
            PreviewImageControl.Source = null;
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using Stream output = stream.AsStreamForWrite();
            await output.WriteAsync(image.Png);
            await output.FlushAsync();
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!_disposed && generation == _previewGeneration)
            {
                PreviewImageControl.Source = bitmap;
                PreviewErrorInfoBar.IsOpen = false;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or COMException
            or IOException
            or InvalidOperationException)
        {
            Log.Warning(
                "Preview failed {ExceptionType} {HResult}",
                exception.GetType().FullName,
                exception.HResult);
            if (!_disposed && generation == _previewGeneration)
            {
                PreviewErrorInfoBar.Message = ResourceText.Get("ErrorPreview");
                PreviewErrorInfoBar.IsOpen = true;
            }
        }
    }
}
