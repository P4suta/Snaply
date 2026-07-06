using System.Reflection;
using Snaply.Core;

namespace Snaply.Tests;

/// <summary>
/// Contract tests for the stable <see cref="ErrorCodes"/> constants. They are a machine-readable
/// API (carried in the JSON envelope, mapped to exit codes, consumed by producers), so their
/// <c>area.reason</c> naming convention, uniqueness, and the specific values that logic branches on
/// are pinned against accidental change.
/// </summary>
public class ErrorCodesContractTests
{
    [Fact]
    public void AllCodes_FollowTheAreaReasonConvention()
    {
        foreach (string code in AllCodes())
        {
            Assert.True(IsAreaReason(code), $"'{code}' is not a valid area.reason code.");
        }
    }

    [Fact]
    public void AllCodes_AreUnique()
    {
        List<string> codes = AllCodes();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("input.invalid")]
    [InlineData("capture.window")]
    [InlineData("capture.window.ambiguous")]
    [InlineData("pipeline.nocapture")]
    public void KnownConsumedCodes_HaveTheirExpectedValues(string expected)
    {
        // Logic in WindowResolver / CapturePipeline / the exit-code map branches on these exact
        // strings; a rename here would silently break those branches.
        Assert.Contains(expected, AllCodes());
    }

    private static List<string> AllCodes() =>
        typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.FullName: "System.String" })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static bool IsAreaReason(string code) =>
        code.Length > 0
        && code.Contains('.', StringComparison.Ordinal)
        && !code.StartsWith('.')
        && !code.EndsWith('.')
        && !code.Contains("..", StringComparison.Ordinal)
        && code.All(c => c is '.' or (>= 'a' and <= 'z'));
}
