using System.CommandLine;

namespace Snaply.Cli;

/// <summary>
/// The four recursive global options (<c>--json</c>/<c>--quiet</c>/<c>--verbose</c>/
/// <c>--no-color</c>) shared by every command, plus the factory that turns them into an
/// <see cref="OutputContext"/>. Centralised so the option instances are added once and read
/// the same way everywhere (no fragile by-name lookups).
/// </summary>
internal static class Globals
{
    /// <summary>Emit machine-readable JSON instead of a human summary.</summary>
    public static readonly Option<bool> Json = new("--json") { Description = "Emit machine-readable JSON instead of a human summary.", Recursive = true };

    /// <summary>Suppress non-essential human output.</summary>
    public static readonly Option<bool> Quiet = new("--quiet", "-q") { Description = "Suppress non-essential output.", Recursive = true };

    /// <summary>Show extra detail such as error causes.</summary>
    public static readonly Option<bool> Verbose = new("--verbose") { Description = "Show extra detail (error causes).", Recursive = true };

    /// <summary>Disable ANSI colour output.</summary>
    public static readonly Option<bool> NoColor = new("--no-color") { Description = "Disable ANSI colour.", Recursive = true };

    /// <summary>Adds the global options to the root command.</summary>
    /// <param name="root">The root command.</param>
    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Options.Add(Json);
        root.Options.Add(Quiet);
        root.Options.Add(Verbose);
        root.Options.Add(NoColor);
    }

    /// <summary>Builds an <see cref="OutputContext"/> from the parsed global options.</summary>
    /// <param name="parseResult">The parse result to read from.</param>
    /// <returns>The configured output context.</returns>
    public static OutputContext Output(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return OutputContext.Create(
            parseResult.GetValue(Json),
            parseResult.GetValue(Quiet),
            parseResult.GetValue(Verbose),
            parseResult.GetValue(NoColor));
    }
}
