using Snaply.Application;

namespace Snaply.Tests;

/// <summary>
/// The MCP capture-consent gate <see cref="CapturePolicy.From"/>. Screen capture is sensitive and
/// off by default; this truth table pins that <c>deny</c> always wins over <c>--allow-capture</c>,
/// that <c>prompt-once</c> demands a per-call confirmation, and that matching is case-insensitive.
/// </summary>
public class CapturePolicyTests
{
    [Theory]
    [InlineData(true, "allow", true, false)]
    [InlineData(true, "prompt-once", true, true)]
    [InlineData(true, "deny", false, false)] // deny overrides --allow-capture
    [InlineData(false, "allow", false, false)] // no --allow-capture: capture stays off
    [InlineData(false, "prompt-once", false, true)]
    [InlineData(true, "DENY", false, false)] // case-insensitive
    [InlineData(true, "PROMPT-ONCE", true, true)]
    [InlineData(true, "something-else", true, false)] // unknown mode: not deny, not prompt-once
    public void From_ResolvesAllowAndConfirm(bool allowCapture, string mode, bool expectedAllow, bool expectedConfirm)
    {
        CapturePolicy policy = CapturePolicy.From(allowCapture, mode);

        Assert.Equal(expectedAllow, policy.AllowCapture);
        Assert.Equal(expectedConfirm, policy.RequireConfirm);
    }
}
