using Snaply.Core;

namespace Snaply.Application;

/// <summary>
/// Maps the domain's stable <see cref="ErrorCodes"/> onto the process exit codes the CLI returns,
/// so scripts and CI can branch on the kind of failure. This is the canonical, documented contract
/// (see <c>docs/CLI.md</c>): 0 is success; 1 an unexpected error; 2 a usage/parse error; every
/// other value is a specific capture/beautify/export/consent failure. It lives in the shared
/// Application layer so the mapping is single-sourced and unit-testable on any platform.
/// </summary>
public static class ExitCodeMap
{
    /// <summary>The command succeeded.</summary>
    public const int Success = 0;

    /// <summary>An unexpected (unhandled) error occurred.</summary>
    public const int Unexpected = 1;

    /// <summary>A usage / argument-parsing / validation error (System.CommandLine default).</summary>
    public const int Usage = 2;

    /// <summary>Resolves the process exit code for a domain <see cref="Error.Code"/>.</summary>
    /// <param name="code">The stable failure code carried by the error.</param>
    /// <returns>The corresponding process exit code.</returns>
    public static int For(string code) => code switch
    {
        ErrorCodes.CaptureMonitor or ErrorCodes.CaptureWindow or ErrorCodes.CaptureRegion => 10,
        ErrorCodes.CaptureWindowAmbiguous => 15,
        ErrorCodes.BeautifyRender => 11,
        ErrorCodes.ExportSave => 12,
        ErrorCodes.ExportClipboard => 13,
        ErrorCodes.PipelineNoCapture => 14,
        ErrorCodes.ConsentDenied => 20,
        ErrorCodes.OutputMissing => 30,
        ErrorCodes.InputInvalid => Usage,
        _ => Unexpected,
    };
}
