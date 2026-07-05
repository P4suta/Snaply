using Snaply.Core;

namespace Snaply.Cli;

/// <summary>
/// Maps the domain's stable <see cref="ErrorCodes"/> onto process exit codes so scripts
/// and CI can branch on the kind of failure. 0 is success; 1 is an unexpected error; 2 is
/// reserved for usage/parse errors (System.CommandLine's default). Everything else is a
/// specific, documented capture/beautify/export/consent failure.
/// </summary>
internal static class ExitCodes
{
    /// <summary>The command succeeded.</summary>
    public const int Success = 0;

    /// <summary>An unexpected (unhandled) error occurred.</summary>
    public const int Unexpected = 1;

    /// <summary>A usage / argument-parsing / validation error (System.CommandLine default).</summary>
    public const int Usage = 2;

    /// <summary>Resolves the exit code for a domain <see cref="Error.Code"/>.</summary>
    /// <param name="code">The stable failure code carried by the error.</param>
    /// <returns>The corresponding process exit code.</returns>
    public static int For(string code) => code switch
    {
        ErrorCodes.CaptureMonitor or ErrorCodes.CaptureWindow or ErrorCodes.CaptureRegion => 10,
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
