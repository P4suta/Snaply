using Snaply.Core.Models;

namespace Snaply.Core.Ports;

/// <summary>
/// Composites a captured screenshot into a beautified image (background, padding,
/// rounded corners, shadow) per a <see cref="BeautifySpec"/>. The Platform layer
/// implements this over Win2D; the "how it should look" maths lives in the pure
/// <c>Snaply.Core.Beautify.BeautifyLayout</c> so it can be tested without a GPU.
/// </summary>
public interface IBeautifyRenderer
{
    /// <summary>Composite <paramref name="source"/> into a beautified image per <paramref name="spec"/>.</summary>
    /// <param name="source">The captured screenshot to beautify.</param>
    /// <param name="spec">How the result should look.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The beautified image, or a failure.</returns>
    Task<Result<CapturedImage>> RenderAsync(CapturedImage source, BeautifySpec spec, CancellationToken cancellationToken = default);
}
