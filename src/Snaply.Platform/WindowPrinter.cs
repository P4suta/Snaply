using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Platform;

/// <summary>
/// Grabs a window via GDI <c>PrintWindow(PW_RENDERFULLCONTENT)</c>, which asks the window to
/// paint itself into a DC without desktop compositing. Unlike the WGC path this never bleeds
/// the desktop into the window's soft edge (rounded corners / border), so the result is opaque
/// and halo-free. Some windows (certain protected / hardware surfaces) render nothing under
/// PrintWindow — <see cref="TryCapture"/> reports that as <c>false</c> so the caller can fall
/// back to WGC. All work is in physical pixels (the app is per-monitor DPI aware).
/// </summary>
internal static class WindowPrinter
{
    // PrintWindow flag: render DWM/hardware-composed content too (plain PrintWindow yields black
    // for modern windows). Available since Windows 8.1.
    private const uint PwRenderFullContent = 0x2;

    // DwmGetWindowAttribute: the visible frame, tighter than the raw window rect (which includes
    // the invisible resize border). PrintWindow paints at the window-rect origin, so the frame is
    // an inset sub-rectangle of the printed bitmap.
    private const int DwmwaExtendedFrameBounds = 9;

    private const uint BiRgb = 0;

    /// <summary>
    /// Try to capture <paramref name="hwnd"/> via PrintWindow, cropped to the visible frame and
    /// forced opaque. Returns <c>false</c> (never throws) when the window is invalid or PrintWindow
    /// draws nothing, so the caller falls back to another capture path.
    /// </summary>
    /// <param name="hwnd">The window handle to capture.</param>
    /// <param name="image">The captured image on success; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a usable image was produced.</returns>
    public static bool TryCapture(nint hwnd, [NotNullWhen(true)] out CapturedImage? image)
    {
        image = null;
        try
        {
            if (!GetWindowRect(hwnd, out RECT wr))
            {
                return false;
            }

            int w = wr.right - wr.left;
            int h = wr.bottom - wr.top;
            if (w <= 0 || h <= 0)
            {
                return false;
            }

            byte[]? bgra = RenderToBgra(hwnd, w, h);
            if (bgra is null)
            {
                return false;
            }

            var windowRect = new PhysicalRect(wr.left, wr.top, w, h);
            PhysicalRect crop = VisibleFrameCrop(hwnd, windowRect, w, h);

            byte[] pixels = CropOpaque(bgra, w, crop);
            image = new CapturedImage(crop.Size, pixels, DpiProvider.GetForWindow(hwnd));
            return true;
        }
        catch (Exception)
        {
            // Any GDI/interop failure just means "PrintWindow didn't work" — let the caller fall back.
            return false;
        }
    }

    /// <summary>Render the whole window rect into a top-down 32bpp DIB and return its BGRA bytes,
    /// or <c>null</c> when PrintWindow fails or draws an empty (all-zero) bitmap.</summary>
    private static byte[]? RenderToBgra(nint hwnd, int w, int h)
    {
        nint screenDc = GetDC(IntPtr.Zero);
        nint memDc = CreateCompatibleDC(screenDc);
        nint dib = IntPtr.Zero;
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // negative => top-down, matching CapturedImage's row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            };

            dib = CreateDIBSection(memDc, ref header, 0, out nint bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            nint previous = SelectObject(memDc, dib);
            bool ok = PrintWindow(hwnd, memDc, PwRenderFullContent);
            _ = GdiFlush();
            SelectObject(memDc, previous);

            if (!ok)
            {
                return null;
            }

            byte[] bgra = new byte[checked(w * h * 4)];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            return HasContent(bgra) ? bgra : null;
        }
        finally
        {
            if (dib != IntPtr.Zero)
            {
                _ = DeleteObject(dib);
            }

            _ = DeleteDC(memDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // The visible frame as an offset inside the window-rect-sized bitmap, clamped to it. Falls back
    // to the whole bitmap when the DWM query fails or yields a degenerate rect.
    private static PhysicalRect VisibleFrameCrop(nint hwnd, PhysicalRect windowRect, int w, int h)
    {
        var full = new PhysicalRect(0, 0, w, h);
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out RECT fr, Marshal.SizeOf<RECT>()) != 0)
        {
            return full;
        }

        var frame = new PhysicalRect(fr.left, fr.top, fr.right - fr.left, fr.bottom - fr.top);
        PhysicalRect crop = frame.RelativeTo(windowRect).Intersect(full);
        return crop.IsEmpty ? full : crop;
    }

    // Copy the crop sub-rectangle into a tightly packed buffer, forcing every pixel opaque (GDI
    // leaves the alpha byte undefined, so the window is treated as fully opaque).
    private static byte[] CropOpaque(byte[] src, int srcWidth, PhysicalRect crop)
    {
        int srcStride = srcWidth * 4;
        int dstStride = crop.Width * 4;
        byte[] dst = new byte[dstStride * crop.Height];
        for (int row = 0; row < crop.Height; row++)
        {
            int srcOffset = ((crop.Y + row) * srcStride) + (crop.X * 4);
            int dstOffset = row * dstStride;
            Array.Copy(src, srcOffset, dst, dstOffset, dstStride);
            for (int a = dstOffset + 3; a < dstOffset + dstStride; a += 4)
            {
                dst[a] = 255;
            }
        }

        return dst;
    }

    // True when any pixel byte is non-zero — PrintWindow leaves the DIB all-zero when it draws nothing.
    private static bool HasContent(byte[] bgra)
    {
        foreach (byte b in bgra)
        {
            if (b != 0)
            {
                return true;
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
}
