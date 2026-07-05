using Snaply.Core.Geometry;

namespace Snaply.Core.Models;

/// <summary>
/// A raw image passed across the ports as plain BGRA bytes — the same layout the
/// GPU capture (DXGI_FORMAT_B8G8R8A8_UNORM) and the encoders use. Keeping the
/// pixels as a byte buffer (not a D3D surface or a WinRT bitmap) is what lets the
/// domain and the renderer's layout logic stay platform-free and unit-testable;
/// the adapters own the conversion to/from GPU and file formats.
/// </summary>
/// <param name="Size">The image dimensions in physical pixels.</param>
/// <param name="Bgra">The pixel buffer in BGRA byte order.</param>
/// <param name="Dpi">The DPI the image was captured at.</param>
public sealed record CapturedImage(PhysicalSize Size, ReadOnlyMemory<byte> Bgra, Dpi Dpi)
{
    /// <summary>Bytes per row (4 bytes/pixel, no extra padding).</summary>
    public int Stride => Size.Width * 4;

    /// <summary>Sanity check that the buffer matches the declared size.</summary>
    public bool IsWellFormed => Bgra.Length == (long)Stride * Size.Height;
}
