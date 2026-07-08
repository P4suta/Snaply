using Snaply.Core.Beautify;
using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// Exercises <see cref="BeautifyLayout.Compute"/>'s aspect-ratio growth — the branch that adds
/// symmetric slack (never cropping) to hit a target ratio and re-centres the image. Sizes and
/// paddings are chosen so the arithmetic lands on exact integers, pinning each formula.
/// </summary>
public class BeautifyLayoutAspectTests
{
    [Fact]
    public void Compute_TooWide_GrowsHeightAndRecentresVertically()
    {
        // 200x100 + no padding = ratio 2.0; Square wants 1.0, so it must grow the HEIGHT.
        // targetH = 200/1 = 200, extra = 100, imageY += 50.
        var spec = new BeautifySpec { Padding = Padding.Uniform(0), Aspect = AspectPreset.Square };

        BeautifyLayoutResult r = BeautifyLayout.Compute(new PhysicalSize(200, 100), spec);

        Assert.Equal(new PhysicalSize(200, 200), r.Canvas);
        Assert.Equal(new PhysicalRect(0, 50, 200, 100), r.Image);
    }

    [Fact]
    public void Compute_TooTall_GrowsWidthAndRecentresHorizontally()
    {
        // 100x200 + no padding = ratio 0.5; Square wants 1.0, so it must grow the WIDTH.
        // targetW = 200*1 = 200, extra = 100, imageX += 50.
        var spec = new BeautifySpec { Padding = Padding.Uniform(0), Aspect = AspectPreset.Square };

        BeautifyLayoutResult r = BeautifyLayout.Compute(new PhysicalSize(100, 200), spec);

        Assert.Equal(new PhysicalSize(200, 200), r.Canvas);
        Assert.Equal(new PhysicalRect(50, 0, 100, 200), r.Image);
    }

    [Fact]
    public void Compute_Wide169_GrowsWidthToTheTargetRatio()
    {
        // 90x90 + no padding = ratio 1.0; Wide wants 16/9, grow WIDTH to 90*16/9 = 160, extra 70.
        var spec = new BeautifySpec { Padding = Padding.Uniform(0), Aspect = AspectPreset.Wide };

        BeautifyLayoutResult r = BeautifyLayout.Compute(new PhysicalSize(90, 90), spec);

        Assert.Equal(new PhysicalSize(160, 90), r.Canvas);
        Assert.Equal(new PhysicalRect(35, 0, 90, 90), r.Image); // imageX += 70/2 = 35
    }

    [Fact]
    public void Compute_Auto_IsExactlySourcePlusPadding()
    {
        // Auto enforces no ratio: canvas is just source + padding, image sits at the top-left pad.
        var spec = new BeautifySpec { Padding = Padding.Uniform(10), Aspect = AspectPreset.Auto };

        BeautifyLayoutResult r = BeautifyLayout.Compute(new PhysicalSize(200, 100), spec);

        Assert.Equal(new PhysicalSize(220, 120), r.Canvas);
        Assert.Equal(new PhysicalRect(10, 10, 200, 100), r.Image);
    }

    [Fact]
    public void Compute_AlreadyAtTargetRatio_LeavesTheCanvasUntouched()
    {
        // 150x150 + no padding is already 1.0, so Square neither widens nor heightens.
        var spec = new BeautifySpec { Padding = Padding.Uniform(0), Aspect = AspectPreset.Square };

        BeautifyLayoutResult r = BeautifyLayout.Compute(new PhysicalSize(150, 150), spec);

        Assert.Equal(new PhysicalSize(150, 150), r.Canvas);
        Assert.Equal(new PhysicalRect(0, 0, 150, 150), r.Image);
    }

    [Fact]
    public void Compute_ThrowsOnNullSpec() =>
        Assert.Throws<ArgumentNullException>(() => BeautifyLayout.Compute(new PhysicalSize(100, 100), null!));
}
