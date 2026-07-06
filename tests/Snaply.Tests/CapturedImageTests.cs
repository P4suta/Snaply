using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// The <see cref="CapturedImage"/> pixel-buffer descriptor: its derived <see cref="CapturedImage.Stride"/>
/// and the <see cref="CapturedImage.IsWellFormed"/> sanity check that guards the capture/encode
/// boundary against a buffer that does not match the declared size.
/// </summary>
public class CapturedImageTests
{
    [Fact]
    public void Stride_IsFourBytesPerPixelRow()
    {
        var image = new CapturedImage(new PhysicalSize(10, 5), new byte[10 * 5 * 4], Dpi.Default);

        Assert.Equal(40, image.Stride);
    }

    [Fact]
    public void IsWellFormed_TrueWhenBufferMatchesSize()
    {
        var image = new CapturedImage(new PhysicalSize(16, 9), new byte[16 * 9 * 4], Dpi.Default);

        Assert.True(image.IsWellFormed);
    }

    [Fact]
    public void IsWellFormed_FalseWhenBufferIsTheWrongSize()
    {
        var image = new CapturedImage(new PhysicalSize(16, 9), new byte[10], Dpi.Default);

        Assert.False(image.IsWellFormed);
    }

    [Fact]
    public void ZeroSize_HasZeroStrideAndIsWellFormed()
    {
        var image = new CapturedImage(new PhysicalSize(0, 0), Array.Empty<byte>(), Dpi.Default);

        Assert.Equal(0, image.Stride);
        Assert.True(image.IsWellFormed);
    }
}
