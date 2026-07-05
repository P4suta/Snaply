using Snaply.Core.Models;

namespace Snaply.Core.Ports;

/// <summary>
/// Loads an image file from disk into the platform-free <see cref="CapturedImage"/> (plain
/// BGRA bytes) so it can be fed back through the beautify pipeline — the CLI's
/// <c>beautify --in</c> verb. Implemented in the Platform layer over Win2D imaging.
/// </summary>
public interface IImageImportService
{
    /// <summary>Decode a PNG/JPEG/… file at <paramref name="path"/> into a raw image.</summary>
    /// <param name="path">The image file to load.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The decoded image as BGRA bytes, or a failure.</returns>
    Task<Result<CapturedImage>> LoadAsync(string path, CancellationToken cancellationToken = default);
}
