using Snaply.Application;
using Snaply.Core;
using Snaply.Core.Geometry;

namespace Snaply.Tests;

/// <summary>
/// The shared <see cref="CaptureArguments"/> parsers used by both the CLI and the MCP server, so a
/// handle printed by one host is accepted by the other. Pins the one intentional difference — a
/// blank handle is a "fall back to the active window" signal for MCP (<c>allowEmpty</c>) but an
/// error for the CLI's <c>--hwnd</c> — in a single place.
/// </summary>
public class CaptureArgumentsTests
{
    [Theory]
    [InlineData("0x10", 16)]
    [InlineData("0X10", 16)] // case-insensitive prefix
    [InlineData("  0x10  ", 16)] // trimmed
    [InlineData("16", 16)] // decimal
    [InlineData("0x402C4", 262852)]
    public void ParseWindowHandle_AcceptsHexAndDecimal(string handle, long expected)
    {
        Result<nint> result = CaptureArguments.ParseWindowHandle(handle, allowEmpty: false);

        Assert.True(result.IsSuccess);
        Assert.Equal((nint)expected, result.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0x0")]
    [InlineData("xyz")]
    [InlineData("0xZZ")]
    public void ParseWindowHandle_RejectsZeroAndMalformed(string handle)
    {
        Result<nint> result = CaptureArguments.ParseWindowHandle(handle, allowEmpty: true);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InputInvalid, result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseWindowHandle_BlankWithAllowEmpty_IsZero(string? handle)
    {
        Result<nint> result = CaptureArguments.ParseWindowHandle(handle, allowEmpty: true);

        Assert.True(result.IsSuccess);
        Assert.Equal((nint)0, result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseWindowHandle_BlankWithoutAllowEmpty_Fails(string? handle)
    {
        Result<nint> result = CaptureArguments.ParseWindowHandle(handle, allowEmpty: false);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InputInvalid, result.Error.Code);
    }

    [Fact]
    public void ParseRegion_ParsesFourIntegers()
    {
        Result<PhysicalRect> result = CaptureArguments.ParseRegion("10,20,300,200");

        Assert.True(result.IsSuccess);
        Assert.Equal(new PhysicalRect(10, 20, 300, 200), result.Value);
    }

    [Fact]
    public void ParseRegion_TrimsWhitespace()
    {
        Result<PhysicalRect> result = CaptureArguments.ParseRegion(" 10, 20, 300, 200 ");

        Assert.True(result.IsSuccess);
        Assert.Equal(new PhysicalRect(10, 20, 300, 200), result.Value);
    }

    [Fact]
    public void ParseRegion_AllowsNegativeOrigin()
    {
        // A region on a monitor left of / above the primary has a negative virtual-desktop origin.
        Result<PhysicalRect> result = CaptureArguments.ParseRegion("-100,-50,200,150");

        Assert.True(result.IsSuccess);
        Assert.Equal(new PhysicalRect(-100, -50, 200, 150), result.Value);
    }

    [Theory]
    [InlineData("1,2,3")] // wrong arity
    [InlineData("a,b,c,d")] // non-numeric
    [InlineData("10,20,0,50")] // zero width
    [InlineData("10,20,50,-1")] // negative height
    [InlineData("")]
    [InlineData(null)]
    public void ParseRegion_RejectsMalformedOrNonPositiveExtent(string? region)
    {
        Result<PhysicalRect> result = CaptureArguments.ParseRegion(region);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InputInvalid, result.Error.Code);
    }
}
