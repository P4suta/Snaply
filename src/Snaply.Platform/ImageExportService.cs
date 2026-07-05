using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Snaply.Core;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace Snaply.Platform;

/// <summary>
/// Saves and shares finished images via Win2D imaging + the WinRT clipboard. PNG
/// keeps the rounded-corner transparency intact.
/// </summary>
public sealed class ImageExportService : IExportService
{
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly ILogger<ImageExportService> _logger;

    /// <summary>Creates the export service.</summary>
    /// <param name="logger">Structured logger for save/clipboard failures.</param>
    public ImageExportService(ILogger<ImageExportService> logger) => _logger = logger;

    /// <inheritdoc/>
    public async Task<Result<string>> SavePngAsync(CapturedImage image, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using CanvasBitmap bitmap = CreateBitmap(image);
            cancellationToken.ThrowIfCancellationRequested();
            await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png).AsTask(cancellationToken).ConfigureAwait(false);
            return Result<string>.Ok(path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PlatformLog.ExportFailed(_logger, ErrorCodes.ExportSave, ex.Message, ex);
            return Result<string>.Fail(ErrorCodes.ExportSave, ex.Message, ex);
        }
    }

    /// <summary>
    /// Copies the image to the clipboard as PNG. MUST be called on a UI thread —
    /// <see cref="Clipboard.SetContent"/> requires the calling thread to have an
    /// initialized clipboard/STA context (the app's DispatcherQueue thread).
    /// </summary>
    /// <param name="image">The image to copy.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>Success, or a failure.</returns>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The InMemoryRandomAccessStream is handed to the clipboard via a RandomAccessStreamReference; the clipboard reads it on demand after this method returns, so ownership transfers to the clipboard and it must not be disposed here.")]
    public async Task<Result> CopyToClipboardAsync(CapturedImage image, CancellationToken cancellationToken = default)
    {
        try
        {
            using CanvasBitmap bitmap = CreateBitmap(image);
            var stream = new InMemoryRandomAccessStream();

            // NO ConfigureAwait(false) here: the continuation must resume on the
            // caller's UI/DispatcherQueue thread, because Clipboard.SetContent below
            // requires it. Moving off-thread here is what caused an access-denied.
            await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask(cancellationToken);
            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PlatformLog.ExportFailed(_logger, ErrorCodes.ExportClipboard, ex.Message, ex);
            return Result.Fail(ErrorCodes.ExportClipboard, ex.Message, ex);
        }
    }

    private CanvasBitmap CreateBitmap(CapturedImage image) => CanvasBitmap.CreateFromBytes(
        _device,
        image.Bgra.ToArray(),
        image.Size.Width,
        image.Size.Height,
        DirectXPixelFormat.B8G8R8A8UIntNormalized);
}
