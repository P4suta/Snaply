using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Platform;

/// <summary>
/// Loads an image file into a <see cref="CapturedImage"/> (plain BGRA bytes) via Win2D
/// imaging, so an on-disk screenshot can be re-fed through the beautify pipeline
/// (the CLI's <c>beautify --in</c> verb).
/// </summary>
public sealed class ImageImportService : IImageImportService
{
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly ILogger<ImageImportService> _logger;

    /// <summary>Creates the image import service.</summary>
    /// <param name="logger">Structured logger for load failures.</param>
    public ImageImportService(ILogger<ImageImportService> logger) => _logger = logger;

    /// <inheritdoc/>
    public async Task<Result<CapturedImage>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<CapturedImage>.Fail(ErrorCodes.InputInvalid, "An input image path is required.");
        }

        if (!File.Exists(path))
        {
            return Result<CapturedImage>.Fail(ErrorCodes.InputInvalid, $"Input image not found: {path}");
        }

        try
        {
            using CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(_device, path).AsTask(cancellationToken).ConfigureAwait(false);
            var size = new PhysicalSize((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            var dpi = bitmap.Dpi > 0 ? new Dpi(bitmap.Dpi) : Dpi.Default;
            return Result<CapturedImage>.Ok(new CapturedImage(size, bitmap.GetPixelBytes(), dpi));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PlatformLog.ImageLoadFailed(_logger, path, ex);
            return Result<CapturedImage>.Fail(ErrorCodes.InputInvalid, ex.Message, ex);
        }
    }
}
