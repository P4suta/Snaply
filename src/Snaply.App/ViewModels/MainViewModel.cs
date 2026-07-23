using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Snaply;

internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ScreenCaptureService _capture;
    private readonly ImageExportService _export;
    private CancellationTokenSource? _operation;
    private RenderedImage? _image;

    [ObservableProperty]
    internal partial WriteableBitmap? Preview { get; set; }

    [ObservableProperty]
    internal partial bool IsBusy { get; set; }

    [ObservableProperty]
    internal partial bool HasImage { get; set; }

    [ObservableProperty]
    internal partial bool HasError { get; set; }

    [ObservableProperty]
    internal partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    internal partial string StatusMessage { get; set; } = ResourceText.Get("StatusReady");

    // Bumped on each successful automatic save; the view watches it to play the folder→green-check
    // "saved" animation (that flip IS the save feedback — there is no toast).
    [ObservableProperty]
    internal partial int SavedTick { get; set; }

    internal MainViewModel(
        ScreenCaptureService capture,
        ImageExportService export)
    {
        _capture = capture;
        _export = export;
    }

    internal CaptureMode LastCaptureMode { get; private set; } = CaptureMode.Region;

    internal async Task CaptureAsync(CaptureMode mode)
    {
        if (IsBusy)
        {
            return;
        }

        LastCaptureMode = mode;
        HasError = false;
        IsBusy = true;
        StatusMessage = ResourceText.Get(mode switch
        {
            CaptureMode.Region => "StatusCapturingRegion",
            CaptureMode.Window => "StatusCapturingWindow",
            _ => "StatusCapturingDesktop",
        });
        using var operation = new CancellationTokenSource();
        _operation = operation;

        try
        {
            using CapturedFrame? frame = await _capture.CaptureAsync(mode, operation.Token);
            if (frame is null)
            {
                StatusMessage = ResourceText.Get("StatusCancelled");
                return;
            }

            RenderedImage image = await BeautifyRenderer.RenderAsync(frame, operation.Token);
            WriteableBitmap preview = await UpdatePreviewAsync(Preview, image, operation.Token);
            _image = image;
            Preview = preview;
            HasImage = true;

            Task<bool> save = TrySaveAutomaticallyAsync(image, operation.Token);
            Task<bool> copy = TryCopyAsync(image, operation.Token);
            await Task.WhenAll(save, copy);
            bool saved = await save;
            bool copied = await copy;
            if (saved)
            {
                SavedTick++;
            }

            StatusMessage = ResourceText.Get((saved, copied) switch
            {
                (true, true) => "StatusSavedCopied",
                (true, false) => "StatusSavedOnly",
                (false, true) => "StatusCopiedOnly",
                _ => "StatusExportFailed",
            });
        }
        catch (OperationCanceledException)
        {
            StatusMessage = ResourceText.Get("StatusCancelled");
        }
        catch (Exception exception)
        {
            LogFailure("Capture", exception);
            ShowError("ErrorCapture");
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }

            IsBusy = false;
        }
    }

    internal void OpenFolder()
    {
        HasError = false;
        try
        {
            _export.OpenCaptureDirectory();
        }
        catch (Exception exception)
        {
            LogFailure("OpenFolder", exception);
            ShowError("ErrorOpenFolder");
        }
    }

    public void Dispose()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
    }

    private static async Task<WriteableBitmap> UpdatePreviewAsync(
        WriteableBitmap? preview,
        RenderedImage image,
        CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(image.Png.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        byte[] pixels = provider.DetachPixelData();
        int expectedLength = checked(checked(image.Width * image.Height) * 4);
        if (pixels.Length != expectedLength)
        {
            throw new InvalidDataException("Decoded preview dimensions are invalid.");
        }

        WriteableBitmap bitmap = preview is not null
            && preview.PixelWidth == image.Width
            && preview.PixelHeight == image.Height
                ? preview
                : new WriteableBitmap(image.Width, image.Height);
        using Stream buffer = bitmap.PixelBuffer.AsStream();
        buffer.Position = 0;
        await buffer.WriteAsync(pixels, cancellationToken);
        await buffer.FlushAsync(cancellationToken);
        bitmap.Invalidate();
        return bitmap;
    }

    private async Task<bool> TrySaveAutomaticallyAsync(
        RenderedImage image,
        CancellationToken cancellationToken)
    {
        try
        {
            await _export.SaveAutomaticallyAsync(image, DateTimeOffset.Now, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure("AutoSave", exception);
            return false;
        }
    }

    private static async Task<bool> TryCopyAsync(
        RenderedImage image,
        CancellationToken cancellationToken)
    {
        try
        {
            await ImageExportService.CopyAsync(image, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure("Clipboard", exception);
            return false;
        }
    }

    private void ShowError(string key)
    {
        ErrorMessage = ResourceText.Get(key);
        HasError = true;
        StatusMessage = ErrorMessage;
    }

    private static void LogFailure(string operation, Exception exception) =>
        Log.Warning(
            "{Operation} failed {ExceptionType} {HResult}",
            operation,
            exception.GetType().FullName,
            exception.HResult);
}
