using Microsoft.Graphics.Canvas;

namespace Snaply;

internal sealed class CapturedFrame : IDisposable
{
    internal CapturedFrame(CanvasBitmap bitmap)
    {
        Bitmap = bitmap;
        Width = checked((int)bitmap.SizeInPixels.Width);
        Height = checked((int)bitmap.SizeInPixels.Height);
    }

    internal CanvasBitmap Bitmap { get; }

    internal int Width { get; }

    internal int Height { get; }

    public void Dispose() => Bitmap.Dispose();
}
