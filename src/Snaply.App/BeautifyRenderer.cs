using System.Numerics;
using System.Security.Cryptography;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Snaply.Imaging;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.UI;

namespace Snaply;

internal static class BeautifyRenderer
{
    internal static async Task<RenderedImage> RenderAsync(
        CapturedFrame source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CanvasDevice device = source.Bitmap.Device;
        BeautifyLayoutResult layout = BeautifyLayout.Compute(new PixelSize(source.Width, source.Height));
        ImageSample sample = SampleImage(device, source.Bitmap);
        ColorPalette palette = ColorPalette.Create(sample.Average, sample.Hash, CreateSalt());
        var imageRect = new Rect(
            layout.Image.X,
            layout.Image.Y,
            layout.Image.Width,
            layout.Image.Height);

        using var rounded = new CanvasRenderTarget(
            device,
            layout.Image.Width,
            layout.Image.Height,
            96);
        using (CanvasDrawingSession drawing = rounded.CreateDrawingSession())
        {
            var local = new Rect(0, 0, layout.Image.Width, layout.Image.Height);
            using CanvasGeometry clip = CanvasGeometry.CreateRoundedRectangle(
                device,
                local,
                layout.CornerRadius,
                layout.CornerRadius);
            using (drawing.CreateLayer(1, clip))
            {
                drawing.DrawImage(source.Bitmap, local);
            }
        }

        using var target = new CanvasRenderTarget(
            device,
            layout.Canvas.Width,
            layout.Canvas.Height,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            using var gradient = new CanvasLinearGradientBrush(
                device,
                ToColor(palette.Start),
                ToColor(palette.End));
            SetGradientDirection(gradient, layout.Canvas, palette.AngleDegrees);
            drawing.FillRectangle(
                new Rect(0, 0, layout.Canvas.Width, layout.Canvas.Height),
                gradient);

            using var shadow = new ShadowEffect
            {
                Source = rounded,
                BlurAmount = layout.ShadowBlur,
                ShadowColor = Color.FromArgb(92, 0, 0, 0),
            };
            drawing.DrawImage(
                shadow,
                new Vector2(
                    layout.Image.X,
                    layout.Image.Y + layout.ShadowOffset));
            drawing.DrawImage(rounded, imageRect);
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png, 1).AsTask(cancellationToken);
        byte[] png = await ReadAllAsync(stream, cancellationToken);
        return RenderedImage.FromOwnedPng(png, layout.Canvas.Width, layout.Canvas.Height);
    }

    private static ImageSample SampleImage(CanvasDevice device, CanvasBitmap source)
    {
        const int sampleSize = 24;
        using var sample = new CanvasRenderTarget(
            device,
            sampleSize,
            sampleSize,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession drawing = sample.CreateDrawingSession())
        {
            drawing.DrawImage(
                source,
                new Rect(0, 0, sampleSize, sampleSize),
                new Rect(0, 0, source.SizeInPixels.Width, source.SizeInPixels.Height),
                1,
                CanvasImageInterpolation.HighQualityCubic);
        }

        byte[] bytes = sample.GetPixelBytes();
        double red = 0;
        double green = 0;
        double blue = 0;
        ulong hash = 1469598103934665603;
        for (int index = 0; index < bytes.Length; index += 4)
        {
            blue += SrgbToLinear(bytes[index] / 255d);
            green += SrgbToLinear(bytes[index + 1] / 255d);
            red += SrgbToLinear(bytes[index + 2] / 255d);
            hash = (hash ^ bytes[index + 2]) * 1099511628211;
            hash = (hash ^ bytes[index + 1]) * 1099511628211;
            hash = (hash ^ bytes[index]) * 1099511628211;
        }

        int pixels = checked(sampleSize * sampleSize);
        return new ImageSample(
            new Rgba(
                LinearToSrgbByte(red / pixels),
                LinearToSrgbByte(green / pixels),
                LinearToSrgbByte(blue / pixels)),
            hash);
    }

    private static async Task<byte[]> ReadAllAsync(
        InMemoryRandomAccessStream stream,
        CancellationToken cancellationToken)
    {
        stream.Seek(0);
        uint length = checked((uint)stream.Size);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint loaded = await reader.LoadAsync(length).AsTask(cancellationToken);
        if (loaded != length)
        {
            throw new EndOfStreamException();
        }

        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static Color ToColor(Rgba color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);

    private static uint CreateSalt()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static double SrgbToLinear(double value) =>
        value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static byte LinearToSrgbByte(double value)
    {
        double clamped = Math.Clamp(value, 0, 1);
        double encoded = clamped <= 0.0031308
            ? clamped * 12.92
            : (1.055 * Math.Pow(clamped, 1 / 2.4)) - 0.055;
        return (byte)Math.Round(encoded * 255, MidpointRounding.AwayFromZero);
    }

    private static void SetGradientDirection(
        CanvasLinearGradientBrush gradient,
        PixelSize canvas,
        double angleDegrees)
    {
        double angle = angleDegrees * Math.PI / 180;
        double x = Math.Cos(angle);
        double y = Math.Sin(angle);
        double centreX = canvas.Width / 2d;
        double centreY = canvas.Height / 2d;
        double extent = (Math.Abs(x) * canvas.Width / 2) + (Math.Abs(y) * canvas.Height / 2);
        gradient.StartPoint = new Vector2(
            (float)(centreX - (x * extent)),
            (float)(centreY - (y * extent)));
        gradient.EndPoint = new Vector2(
            (float)(centreX + (x * extent)),
            (float)(centreY + (y * extent)));
    }

    private readonly record struct ImageSample(Rgba Average, ulong Hash);
}
