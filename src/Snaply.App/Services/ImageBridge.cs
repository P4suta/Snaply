using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Snaply.Core.Models;

namespace Snaply.Services;

/// <summary>
/// The one place the platform-free <see cref="CapturedImage"/> (raw BGRA bytes) is
/// turned into a XAML <see cref="Microsoft.UI.Xaml.Media.ImageSource"/> for preview.
/// Kept out of the ViewModel so the VM never touches a WinUI imaging type.
/// </summary>
public static class ImageBridge
{
    /// <summary>
    /// Copy a captured image's BGRA bytes into a <see cref="WriteableBitmap"/>.
    /// The buffer layout (B8G8R8A8, premultiplied, tight stride) matches exactly
    /// what <see cref="WriteableBitmap.PixelBuffer"/> expects.
    /// </summary>
    /// <param name="image">The captured image to convert.</param>
    /// <returns>A <see cref="WriteableBitmap"/> holding the image's pixels.</returns>
    public static WriteableBitmap ToWriteableBitmap(CapturedImage image)
    {
        var bitmap = new WriteableBitmap(image.Size.Width, image.Size.Height);
        byte[] bytes = image.Bgra.ToArray();
        using Stream stream = bitmap.PixelBuffer.AsStream();
        stream.Write(bytes, 0, bytes.Length);
        bitmap.Invalidate();
        return bitmap;
    }
}
