using Snaply.Core.Beautify;
using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// These run with no display and no GPU — that they can assert on the beautify
/// geometry at all is the proof that the visual logic is cleanly separated from
/// the Win2D renderer (the architecture fitness goal).
/// </summary>
public class BeautifyLayoutTests
{
    [Fact]
    public void UniformPadding_GrowsCanvasAndOffsetsImage()
    {
        var spec = BeautifySpec.Default with { Padding = Padding.Uniform(64), Aspect = AspectPreset.Auto };

        var result = BeautifyLayout.Compute(new PhysicalSize(800, 600), spec);

        Assert.Equal(new PhysicalSize(928, 728), result.Canvas);
        Assert.Equal(new PhysicalRect(64, 64, 800, 600), result.Image);
    }

    [Fact]
    public void SquareAspect_AddsSymmetricSlackAndCentresImage()
    {
        var spec = BeautifySpec.Default with { Padding = Padding.Uniform(0), Aspect = AspectPreset.Square };

        var result = BeautifyLayout.Compute(new PhysicalSize(800, 600), spec);

        // 800x600 -> square 800x800, image centred vertically (100px slack top & bottom).
        Assert.Equal(new PhysicalSize(800, 800), result.Canvas);
        Assert.Equal(new PhysicalRect(0, 100, 800, 600), result.Image);
    }

    [Fact]
    public void AspectNeverCropsTheScreenshot()
    {
        var spec = BeautifySpec.Default with { Padding = Padding.Uniform(32), Aspect = AspectPreset.Wide };

        var result = BeautifyLayout.Compute(new PhysicalSize(1000, 1000), spec);

        // The image keeps its full pixels; only the canvas grows around it.
        Assert.Equal(1000, result.Image.Width);
        Assert.Equal(1000, result.Image.Height);
        Assert.True(result.Canvas.Width >= 1000 + 64);
        Assert.True(result.Canvas.Height >= 1000 + 64);
    }
}

/// <summary>Crispness lives or dies on this conversion: logical rects must land on exact physical pixels.</summary>
public class GeometryTests
{
    [Fact]
    public void LogicalRect_At150PercentScale_MapsToPhysicalPixels()
    {
        var dpi = new Dpi(144); // 150%
        var logical = new LogicalRect(0, 0, 1280, 800);

        var physical = logical.ToPhysical(dpi);

        Assert.Equal(new PhysicalRect(0, 0, 1920, 1200), physical);
    }

    [Fact]
    public void LogicalRect_At100PercentScale_IsIdentity()
    {
        var physical = new LogicalRect(10, 20, 300, 200).ToPhysical(Dpi.Default);

        Assert.Equal(new PhysicalRect(10, 20, 300, 200), physical);
    }
}
