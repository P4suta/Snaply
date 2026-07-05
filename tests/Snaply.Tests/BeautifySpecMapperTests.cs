using Snaply.Application;
using Snaply.Core;
using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// Tests for the shared <see cref="BeautifySpecMapper"/> that turns the CLI/MCP string options
/// into a Core <see cref="BeautifySpec"/>. Validates the grammar both hosts expose and that
/// bad input becomes an <see cref="ErrorCodes.InputInvalid"/> failure rather than an exception.
/// </summary>
public class BeautifySpecMapperTests
{
    [Fact]
    public void NoOptions_YieldsTheDefaultSpec()
    {
        Result<BeautifySpec?> result = BeautifySpecMapper.Map(new BeautifyOptions());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(BeautifySpec.Default.Background, result.Value!.Background);
    }

    [Fact]
    public void NoBeautify_YieldsNullSpec()
    {
        Result<BeautifySpec?> result = BeautifySpecMapper.Map(new BeautifyOptions(NoBeautify: true));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value); // null == keep the raw capture
    }

    [Fact]
    public void Background_Auto_MapsToAutoMarker()
    {
        BeautifySpec spec = MapOk(new BeautifyOptions(Background: "auto"));
        Assert.IsType<Background.Auto>(spec.Background);
    }

    [Fact]
    public void Background_Solid_ParsesHex()
    {
        BeautifySpec spec = MapOk(new BeautifyOptions(Background: "solid:#ff8800"));
        var solid = Assert.IsType<Background.Solid>(spec.Background);
        Assert.Equal(new Rgba(255, 136, 0, 255), solid.Color);
    }

    [Fact]
    public void Background_Gradient_ParsesTwoColoursAndAngle()
    {
        BeautifySpec spec = MapOk(new BeautifyOptions(Background: "gradient:#000000,#ffffff@90"));
        var gradient = Assert.IsType<Background.LinearGradient>(spec.Background);
        Assert.Equal(new Rgba(0, 0, 0, 255), gradient.Start);
        Assert.Equal(new Rgba(255, 255, 255, 255), gradient.End);
        Assert.Equal(90, gradient.AngleDegrees);
    }

    [Fact]
    public void Background_Image_MapsToImageFile()
    {
        BeautifySpec spec = MapOk(new BeautifyOptions(Background: "image:C:/wall.png"));
        var image = Assert.IsType<Background.ImageFile>(spec.Background);
        Assert.Equal("C:/wall.png", image.Path);
    }

    [Theory]
    [InlineData("#abc", 170, 187, 204, 255)] // #RGB shorthand expands
    [InlineData("#1e3a8a", 30, 58, 138, 255)] // #RRGGBB
    [InlineData("#1e3a8a80", 30, 58, 138, 128)] // #RRGGBBAA
    public void Color_AcceptsShortLongAndAlphaHex(string hex, byte r, byte g, byte b, byte a)
    {
        Result<Rgba> color = BeautifySpecMapper.ParseColor(hex);
        Assert.True(color.IsSuccess);
        Assert.Equal(new Rgba(r, g, b, a), color.Value);
    }

    [Fact]
    public void Padding_Uniform_And_PerEdge()
    {
        Assert.Equal(Padding.Uniform(48), MapOk(new BeautifyOptions(Padding: "48")).Padding);
        Assert.Equal(new Padding(1, 2, 3, 4), MapOk(new BeautifyOptions(Padding: "1,2,3,4")).Padding);
    }

    [Fact]
    public void ExplicitPaddingAndCorner_DisableAutoDerivation()
    {
        // An explicit --padding / --corner-radius must switch off the pipeline's auto-derivation,
        // otherwise the user's value is silently discarded.
        BeautifySpec padded = MapOk(new BeautifyOptions(Padding: "200"));
        Assert.False(padded.AutoPadding);
        Assert.Equal(Padding.Uniform(200), padded.Padding);

        BeautifySpec rounded = MapOk(new BeautifyOptions(CornerRadius: 50));
        Assert.False(rounded.AutoCornerRadius);
        Assert.Equal(50, rounded.CornerRadius);

        // Unspecified stays on auto.
        BeautifySpec bare = MapOk(new BeautifyOptions());
        Assert.True(bare.AutoPadding);
        Assert.True(bare.AutoCornerRadius);
    }

    [Fact]
    public void Shadow_NoneAndDefaultAndCustom()
    {
        Assert.Equal(ShadowSpec.None, MapOk(new BeautifyOptions(Shadow: "none")).Shadow);
        Assert.Equal(ShadowSpec.Default, MapOk(new BeautifyOptions(Shadow: "default")).Shadow);

        ShadowSpec custom = MapOk(new BeautifyOptions(Shadow: "4,8,20,0.5")).Shadow;
        Assert.Equal(4, custom.OffsetX);
        Assert.Equal(8, custom.OffsetY);
        Assert.Equal(20, custom.BlurRadius);
        Assert.Equal(0.5, custom.Opacity);
    }

    [Theory]
    [InlineData("wide", AspectPreset.Wide)]
    [InlineData("SQUARE", AspectPreset.Square)]
    [InlineData("standard", AspectPreset.Standard)]
    public void Aspect_IsCaseInsensitive(string value, AspectPreset expected)
    {
        Assert.Equal(expected, MapOk(new BeautifyOptions(Aspect: value)).Aspect);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("solid:not-a-colour")]
    [InlineData("gradient:#000000")]
    public void Background_Invalid_FailsWithInputInvalid(string background)
    {
        Result<BeautifySpec?> result = BeautifySpecMapper.Map(new BeautifyOptions(Background: background));
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InputInvalid, result.Error.Code);
    }

    [Fact]
    public void Aspect_Invalid_FailsWithInputInvalid()
    {
        Result<BeautifySpec?> result = BeautifySpecMapper.Map(new BeautifyOptions(Aspect: "panoramic"));
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InputInvalid, result.Error.Code);
    }

    [Fact]
    public void Gradient_WithoutAngle_DefaultsTo135()
    {
        var gradient = Assert.IsType<Background.LinearGradient>(MapOk(new BeautifyOptions(Background: "gradient:#000000,#ffffff")).Background);
        Assert.Equal(135, gradient.AngleDegrees);
    }

    [Fact]
    public void Shadow_WithExplicitColour_IsParsed()
    {
        ShadowSpec shadow = MapOk(new BeautifyOptions(Shadow: "2,4,10,0.25,#ff0000")).Shadow;
        Assert.Equal(new Rgba(255, 0, 0, 255), shadow.Color);
        Assert.Equal(0.25, shadow.Opacity);
    }

    [Theory]
    [InlineData("image:")] // missing path
    [InlineData("padding")] // unknown background kind
    public void Background_MalformedVariants_Fail(string background)
    {
        Assert.True(BeautifySpecMapper.Map(new BeautifyOptions(Background: background)).IsFailure);
    }

    [Theory]
    [InlineData("1,2,3")] // wrong arity
    [InlineData("-5")] // negative
    [InlineData("x")] // non-numeric
    public void Padding_Invalid_Fails(string padding)
    {
        Result<BeautifySpec?> result = BeautifySpecMapper.Map(new BeautifyOptions(Padding: padding));
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InputInvalid, result.Error.Code);
    }

    [Fact]
    public void NegativeCornerRadius_Fails()
    {
        Assert.True(BeautifySpecMapper.Map(new BeautifyOptions(CornerRadius: -1)).IsFailure);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1,2,3")] // too few numbers
    public void Shadow_Invalid_Fails(string shadow)
    {
        Assert.True(BeautifySpecMapper.Map(new BeautifyOptions(Shadow: shadow)).IsFailure);
    }

    private static BeautifySpec MapOk(BeautifyOptions options)
    {
        Result<BeautifySpec?> result = BeautifySpecMapper.Map(options);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.NotNull(result.Value);
        return result.Value!;
    }
}
