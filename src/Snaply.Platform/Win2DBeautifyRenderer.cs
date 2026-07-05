using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Snaply.Core;
using Snaply.Core.Beautify;
using Snaply.Core.Models;
using Snaply.Core.Ports;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Snaply.Platform;

/// <summary>
/// Composites a captured screenshot into a beautified image with Win2D. All geometry
/// (canvas size, image placement) comes from the pure <see cref="BeautifyLayout"/>;
/// this adapter only draws what that returns — background, drop shadow, rounded image.
/// </summary>
public sealed class Win2DBeautifyRenderer : IBeautifyRenderer
{
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly ILogger<Win2DBeautifyRenderer> _logger;

    /// <summary>Creates the beautify renderer.</summary>
    /// <param name="logger">Structured logger for render failures.</param>
    public Win2DBeautifyRenderer(ILogger<Win2DBeautifyRenderer> logger) => _logger = logger;

    /// <inheritdoc/>
    public async Task<Result<CapturedImage>> RenderAsync(CapturedImage source, BeautifySpec spec, CancellationToken cancellationToken = default)
    {
        try
        {
            BeautifyLayoutResult layout = BeautifyLayout.Compute(source.Size, spec);
            var imageRect = new Rect(layout.Image.X, layout.Image.Y, layout.Image.Width, layout.Image.Height);

            // Load an ImageFile background up front so the drawing session stays synchronous.
            CanvasBitmap? backgroundBitmap = null;
            if (spec.Background is Background.ImageFile imageFile)
            {
                try
                {
                    backgroundBitmap = await CanvasBitmap.LoadAsync(_device, imageFile.Path).AsTask(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PlatformLog.BeautifyBackgroundLoadFailed(_logger, imageFile.Path, ex);
                    backgroundBitmap = null; // fall back to a solid fill below
                }
            }

            using CanvasBitmap sourceBitmap = CanvasBitmap.CreateFromBytes(
                _device,
                source.Bgra.ToArray(),
                source.Size.Width,
                source.Size.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);

            // Pre-render the rounded screenshot once; it is reused for the shadow silhouette.
            using var rounded = new CanvasRenderTarget(_device, layout.Image.Width, layout.Image.Height, 96);
            using (CanvasDrawingSession rs = rounded.CreateDrawingSession())
            {
                var localRect = new Rect(0, 0, layout.Image.Width, layout.Image.Height);
                using CanvasGeometry clip = CanvasGeometry.CreateRoundedRectangle(
                    _device, localRect, (float)spec.CornerRadius, (float)spec.CornerRadius);
                using (rs.CreateLayer(1f, clip))
                {
                    rs.DrawImage(sourceBitmap, localRect);
                }
            }

            using var target = new CanvasRenderTarget(_device, layout.Canvas.Width, layout.Canvas.Height, 96);
            using (CanvasDrawingSession ds = target.CreateDrawingSession())
            {
                DrawBackground(ds, spec.Background, layout.Canvas, backgroundBitmap);

                if (spec.Shadow.Opacity > 0 && spec.Shadow.Color.A > 0)
                {
                    using var shadow = new ShadowEffect
                    {
                        Source = rounded,
                        BlurAmount = (float)(spec.Shadow.BlurRadius / 3.0),
                        ShadowColor = ToColor(spec.Shadow.Color, spec.Shadow.Opacity),
                    };
                    ds.DrawImage(shadow, new Vector2(
                        (float)(imageRect.X + spec.Shadow.OffsetX),
                        (float)(imageRect.Y + spec.Shadow.OffsetY)));
                }

                ds.DrawImage(rounded, imageRect);
            }

            backgroundBitmap?.Dispose();

            return Result<CapturedImage>.Ok(new CapturedImage(layout.Canvas, target.GetPixelBytes(), source.Dpi));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PlatformLog.BeautifyRenderFailed(_logger, ex.Message, ex);
            return Result<CapturedImage>.Fail(ErrorCodes.BeautifyRender, ex.Message, ex);
        }
    }

    private void DrawBackground(CanvasDrawingSession ds, Background background, Snaply.Core.Geometry.PhysicalSize canvas, CanvasBitmap? backgroundBitmap)
    {
        switch (background)
        {
            case Background.Solid solid:
                ds.Clear(ToColor(solid.Color));
                break;

            case Background.LinearGradient gradient:
                using (var brush = new CanvasLinearGradientBrush(_device, ToColor(gradient.Start), ToColor(gradient.End)))
                {
                    double angle = gradient.AngleDegrees * Math.PI / 180.0;
                    double dx = Math.Cos(angle);
                    double dy = Math.Sin(angle);
                    double cx = canvas.Width / 2.0;
                    double cy = canvas.Height / 2.0;

                    // Project the canvas half-extent onto the gradient axis so the two
                    // stops span the full canvas at any angle.
                    double half = (Math.Abs(dx) * canvas.Width / 2.0) + (Math.Abs(dy) * canvas.Height / 2.0);
                    brush.StartPoint = new Vector2((float)(cx - (dx * half)), (float)(cy - (dy * half)));
                    brush.EndPoint = new Vector2((float)(cx + (dx * half)), (float)(cy + (dy * half)));
                    ds.FillRectangle(new Rect(0, 0, canvas.Width, canvas.Height), brush);
                }

                break;

            case Background.ImageFile:
                if (backgroundBitmap is not null)
                {
                    DrawCover(ds, backgroundBitmap, canvas);
                }
                else
                {
                    ds.Clear(ToColor(Rgba.White));
                }

                break;
        }
    }

    private static void DrawCover(CanvasDrawingSession ds, CanvasBitmap bitmap, Snaply.Core.Geometry.PhysicalSize canvas)
    {
        double imgW = bitmap.SizeInPixels.Width;
        double imgH = bitmap.SizeInPixels.Height;
        double scale = Math.Max(canvas.Width / imgW, canvas.Height / imgH);
        double drawW = imgW * scale;
        double drawH = imgH * scale;
        double x = (canvas.Width - drawW) / 2.0;
        double y = (canvas.Height - drawH) / 2.0;
        ds.DrawImage(bitmap, new Rect(x, y, drawW, drawH));
    }

    private static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static Color ToColor(Rgba c, double opacity)
    {
        byte a = (byte)Math.Clamp(Math.Round(c.A * opacity), 0, 255);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }
}
