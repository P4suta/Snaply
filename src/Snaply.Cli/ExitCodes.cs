using Snaply.Application;
using Snaply.Core;

namespace Snaply.Cli;

/// <summary>
/// The CLI's process exit codes. A thin host-side alias over the canonical
/// <see cref="ExitCodeMap"/> in the shared Application layer, so the CLI keeps its ergonomic
/// <c>ExitCodes.Success</c> / <c>ExitCodes.For(...)</c> call sites while the mapping stays
/// single-sourced and unit-testable. See <c>docs/CLI.md</c> for the documented contract.
/// </summary>
internal static class ExitCodes
{
    /// <summary>The command succeeded.</summary>
    public const int Success = ExitCodeMap.Success;

    /// <summary>An unexpected (unhandled) error occurred.</summary>
    public const int Unexpected = ExitCodeMap.Unexpected;

    /// <summary>A usage / argument-parsing / validation error (System.CommandLine default).</summary>
    public const int Usage = ExitCodeMap.Usage;

    /// <summary>Resolves the exit code for a domain <see cref="Error.Code"/>.</summary>
    /// <param name="code">The stable failure code carried by the error.</param>
    /// <returns>The corresponding process exit code.</returns>
    public static int For(string code) => ExitCodeMap.For(code);
}
