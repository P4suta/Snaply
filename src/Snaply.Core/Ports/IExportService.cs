using Snaply.Core.Models;

namespace Snaply.Core.Ports;

/// <summary>
/// Persists or shares a finished image. Implemented in the Platform layer over the
/// WinRT imaging + clipboard APIs; PNG keeps the result lossless with its alpha
/// (the rounded-corner transparency) intact.
/// </summary>
public interface IExportService
{
    /// <summary>Save as a lossless PNG; returns the written path on success.</summary>
    /// <param name="image">The image to write.</param>
    /// <param name="path">The destination file path.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The written path, or a failure.</returns>
    Task<Result<string>> SavePngAsync(CapturedImage image, string path, CancellationToken cancellationToken = default);

    /// <summary>Copy the image to the system clipboard as PNG.</summary>
    /// <param name="image">The image to copy.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>Success, or a failure.</returns>
    Task<Result> CopyToClipboardAsync(CapturedImage image, CancellationToken cancellationToken = default);
}
