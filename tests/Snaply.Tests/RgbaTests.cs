using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// The Core colour type <see cref="Rgba"/> — its factory, named constants, and value equality.
/// Exercised indirectly by the beautify mapper, pinned directly here so a channel-order or
/// alpha-default regression is caught at the source.
/// </summary>
public class RgbaTests
{
    [Fact]
    public void FromRgb_SetsChannelsAndOpaqueAlpha()
    {
        Assert.Equal(new Rgba(10, 20, 30, 255), Rgba.FromRgb(10, 20, 30));
    }

    [Fact]
    public void Transparent_IsFullyTransparentBlack()
    {
        Assert.Equal(new Rgba(0, 0, 0, 0), Rgba.Transparent);
    }

    [Fact]
    public void White_IsOpaqueWhite()
    {
        Assert.Equal(new Rgba(255, 255, 255, 255), Rgba.White);
    }

    [Fact]
    public void Equality_IsByValue_AndAlphaSensitive()
    {
        Assert.Equal(new Rgba(1, 2, 3, 4), new Rgba(1, 2, 3, 4));
        Assert.NotEqual(new Rgba(1, 2, 3, 255), new Rgba(1, 2, 3, 128));
    }
}
