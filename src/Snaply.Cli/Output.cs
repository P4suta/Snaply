using System.Text.Json;
using System.Text.Json.Serialization;
using Snaply.Core;
using Spectre.Console;

namespace Snaply.Cli;

/// <summary>The machine-readable JSON envelope shared by every command's <c>--json</c> output.</summary>
/// <param name="Ok">Whether the command succeeded.</param>
/// <param name="Command">The dotted command name (e.g. <c>capture.region</c>).</param>
/// <param name="Data">The success payload, present when <paramref name="Ok"/> is true.</param>
/// <param name="Error">The failure detail, present when <paramref name="Ok"/> is false.</param>
internal sealed record Envelope(bool Ok, string Command, object? Data, ErrorInfo? Error);

/// <summary>Machine-readable failure detail: the stable code, a message, and the process exit code.</summary>
/// <param name="Code">The stable <see cref="ErrorCodes"/> value.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="ExitCode">The process exit code the CLI will return.</param>
internal sealed record ErrorInfo(string Code, string Message, int ExitCode);

/// <summary>
/// Owns all user-facing output for the CLI: a Spectre console for humans (colour, tables,
/// spinners) and a System.Text.Json envelope for machines (<c>--json</c>). Human status is
/// written to <c>stderr</c> so it never mixes with piped data on <c>stdout</c>; when a command
/// streams raw bytes to <c>stdout</c> (<c>--stdout</c>) all human text is redirected to stderr.
/// </summary>
internal sealed class OutputContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly IAnsiConsole _stdout;
    private readonly IAnsiConsole _stderr;

    private OutputContext(bool json, bool quiet, bool verbose, bool noColor)
    {
        Json = json;
        Quiet = quiet;
        Verbose = verbose;
        _stdout = MakeConsole(Console.Out, noColor);
        _stderr = MakeConsole(Console.Error, noColor);
    }

    /// <summary>Whether machine-readable JSON output is requested.</summary>
    public bool Json { get; }

    /// <summary>Whether non-essential human output (spinners, summaries) is suppressed.</summary>
    public bool Quiet { get; }

    /// <summary>Whether extra detail (error causes, timings) is shown.</summary>
    public bool Verbose { get; }

    /// <summary>When true, human output is kept off <c>stdout</c> (a command is streaming bytes there).</summary>
    public bool RedirectHumanToErr { get; set; }

    /// <summary>The console human summaries are written to (stdout, unless redirected to stderr).</summary>
    public IAnsiConsole Human => RedirectHumanToErr ? _stderr : _stdout;

    /// <summary>The console for status/progress (always stderr, so it never pollutes piped data).</summary>
    public IAnsiConsole Status => _stderr;

    /// <summary>Builds an output context from the parsed global options.</summary>
    /// <param name="json">Whether <c>--json</c> was set.</param>
    /// <param name="quiet">Whether <c>--quiet</c> was set.</param>
    /// <param name="verbose">Whether <c>--verbose</c> was set.</param>
    /// <param name="noColor">Whether <c>--no-color</c> was set (or <c>NO_COLOR</c> is present).</param>
    /// <returns>The configured output context.</returns>
    public static OutputContext Create(bool json, bool quiet, bool verbose, bool noColor)
    {
        bool suppressColor = noColor || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        return new OutputContext(json, quiet, verbose, suppressColor);
    }

    /// <summary>
    /// Emits a success result. In JSON mode writes the ok envelope to stdout; otherwise invokes
    /// <paramref name="human"/> to render a rich summary (unless <see cref="Quiet"/>).
    /// </summary>
    /// <param name="command">The dotted command name.</param>
    /// <param name="data">The JSON data payload.</param>
    /// <param name="human">Renders the human summary onto the given console.</param>
    /// <returns>The success exit code.</returns>
    public async Task<int> SuccessAsync(string command, object data, Action<IAnsiConsole> human)
    {
        if (Json)
        {
            await WriteJsonAsync(new Envelope(true, command, data, null)).ConfigureAwait(true);
        }
        else if (!Quiet)
        {
            human(Human);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Emits a failure result and returns the mapped exit code. In JSON mode writes the error
    /// envelope to stdout; otherwise renders a red panel to stderr (with the cause if verbose).
    /// </summary>
    /// <param name="command">The dotted command name.</param>
    /// <param name="error">The domain error.</param>
    /// <returns>The mapped process exit code.</returns>
    public async Task<int> FailAsync(string command, Error error)
    {
        int exit = ExitCodes.For(error.Code);
        if (Json)
        {
            await WriteJsonAsync(new Envelope(false, command, null, new ErrorInfo(error.Code, error.Message, exit))).ConfigureAwait(true);
            return exit;
        }

        string detail = Markup.Escape(error.Message);
        if (Verbose && error.Cause is not null)
        {
            detail += "\n\n" + Markup.Escape(error.Cause.ToString());
        }

        _stderr.Write(new Panel(new Markup($"[red]{Markup.Escape(error.Code)}[/]\n{detail}"))
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader(" error ", Justify.Left),
            BorderStyle = new Style(Color.Red),
        });
        return exit;
    }

    private async Task WriteJsonAsync(Envelope envelope)
    {
        string json = JsonSerializer.Serialize(envelope, JsonOptions);

        // When a command is streaming raw PNG bytes to stdout (--stdout), the JSON envelope must
        // go to stderr so the two don't interleave on the same stream.
        TextWriter target = RedirectHumanToErr ? Console.Error : Console.Out;
        await target.WriteLineAsync(json).ConfigureAwait(true);
    }

    private static IAnsiConsole MakeConsole(TextWriter writer, bool noColor) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = noColor ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Out = new AnsiConsoleOutput(writer),
        });
}
