using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// Pins the defaults and aspect-ratio table on <see cref="BeautifySpec"/> — the zero-config
/// behaviour the "auto everything" UI relies on. Loose tests never notice a flipped default or a
/// mistyped ratio; these assert the exact values.
/// </summary>
public class BeautifySpecDefaultsTests
{
    [Fact]
    public void Defaults_AutoDerivePaddingAndCornerRadius()
    {
        BeautifySpec spec = BeautifySpec.Default;

        Assert.True(spec.AutoPadding);
        Assert.True(spec.AutoCornerRadius);
        Assert.Equal(AspectPreset.Auto, spec.Aspect);
    }

    [Fact]
    public void FreshSpec_MatchesTheDocumentedDefaults()
    {
        var spec = new BeautifySpec();

        Assert.True(spec.AutoPadding);
        Assert.True(spec.AutoCornerRadius);
        Assert.Equal(16, spec.CornerRadius);
        Assert.Equal(Padding.Uniform(64), spec.Padding);
    }

    [Theory]
    [InlineData(AspectPreset.Square, 1.0)]
    [InlineData(AspectPreset.Standard, 4.0 / 3.0)]
    [InlineData(AspectPreset.Wide, 16.0 / 9.0)]
    public void Ratio_ResolvesEachPresetExactly(AspectPreset preset, double expected) =>
        Assert.Equal(expected, preset.Ratio());

    [Fact]
    public void Ratio_IsNullForAuto() =>
        Assert.Null(AspectPreset.Auto.Ratio());
}
