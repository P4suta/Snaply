namespace Snaply.Application;

/// <summary>
/// The consent policy for the MCP server's screen-capture tools. Screen capture is sensitive,
/// so it is <b>off by default</b>: a client can only capture when the operator started the
/// server with <c>--allow-capture</c>, and (unless <c>--consent-mode allow</c>) each capture
/// must additionally pass <c>confirmed: true</c>. Read-only <c>list_*</c> tools are unaffected.
/// Lives in the shared Application layer so the decision is host-agnostic and unit-testable.
/// </summary>
/// <param name="AllowCapture">Whether capture tools may run at all.</param>
/// <param name="RequireConfirm">Whether each capture call must pass <c>confirmed: true</c>.</param>
public sealed record CapturePolicy(bool AllowCapture, bool RequireConfirm)
{
    /// <summary>Resolves the policy from the CLI flags.</summary>
    /// <param name="allowCapture">Whether <c>--allow-capture</c> was set.</param>
    /// <param name="consentMode"><c>deny</c> | <c>prompt-once</c> | <c>allow</c>.</param>
    /// <returns>The resolved policy.</returns>
    public static CapturePolicy From(bool allowCapture, string consentMode)
    {
        bool deny = string.Equals(consentMode, "deny", StringComparison.OrdinalIgnoreCase);
        bool promptOnce = string.Equals(consentMode, "prompt-once", StringComparison.OrdinalIgnoreCase);
        return new CapturePolicy(allowCapture && !deny, promptOnce);
    }
}
