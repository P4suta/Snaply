using System.CommandLine;

namespace Snaply.Cli;

/// <summary>
/// The <c>snaply completions</c> command: prints a shell completion script for
/// bash / zsh / pwsh / fish to stdout, or writes all four to a directory with
/// <c>--all</c>. Generating the scripts in C# (rather than a shell loop in the justfile)
/// keeps the automation durable and testable. (Deliberately a static script for the common
/// verbs; deep per-option completion is left to a future iteration.)
/// </summary>
internal static class CompletionsCommand
{
    private const string Commands = "capture beautify list doctor completions mcp";

    private static readonly string[] Shells = ["bash", "zsh", "pwsh", "fish"];

    /// <summary>Builds the <c>completions</c> command.</summary>
    /// <returns>The configured command.</returns>
    public static Command Build()
    {
        var shellArgument = new Argument<string?>("shell") { Description = "bash | zsh | pwsh | fish", Arity = ArgumentArity.ZeroOrOne };
        shellArgument.AcceptOnlyFromAmong(Shells);
        var allOption = new Option<bool>("--all") { Description = "Write every shell's script to --out instead of printing one." };
        var outOption = new Option<string>("--out") { Description = "Output directory for --all.", DefaultValueFactory = _ => "build/completions" };

        var completions = new Command("completions", "Print (or write) a shell completion script.") { shellArgument, allOption, outOption };
        completions.SetAction(async (parseResult, ct) =>
        {
            bool all = parseResult.GetValue(allOption);
            string? shell = parseResult.GetValue(shellArgument);

            if (all)
            {
                string outDir = parseResult.GetValue(outOption)!;
                Directory.CreateDirectory(outDir);
                foreach (string s in Shells)
                {
                    string path = Path.Combine(outDir, $"snaply.{s}");
                    await File.WriteAllTextAsync(path, Script(s), ct).ConfigureAwait(true);
                    await Console.Out.WriteLineAsync(path).ConfigureAwait(true);
                }

                return ExitCodes.Success;
            }

            if (shell is null)
            {
                await Console.Error.WriteLineAsync("Specify a shell (bash|zsh|pwsh|fish) or --all.").ConfigureAwait(true);
                return ExitCodes.Usage;
            }

            await Console.Out.WriteLineAsync(Script(shell)).ConfigureAwait(true);
            return ExitCodes.Success;
        });
        return completions;
    }

    private static string Script(string shell) => shell switch
    {
        "bash" => Bash,
        "zsh" => Zsh,
        "pwsh" => Pwsh,
        "fish" => Fish,
        _ => string.Empty,
    };

    private static string Bash =>
        "# snaply bash completion — add to ~/.bashrc: source <(snaply completions bash)\n" +
        "_snaply() {\n" +
        "  local cur=\"${COMP_WORDS[COMP_CWORD]}\"\n" +
        $"  COMPREPLY=( $(compgen -W \"{Commands} --json --quiet --verbose --no-color --help --version\" -- \"$cur\") )\n" +
        "}\n" +
        "complete -F _snaply snaply";

    private static string Zsh =>
        "# snaply zsh completion — add to ~/.zshrc: source <(snaply completions zsh)\n" +
        $"compctl -k \"({Commands} --json --quiet --verbose --no-color --help --version)\" snaply";

    private static string Pwsh =>
        "# snaply pwsh completion — add to $PROFILE: snaply completions pwsh | Out-String | Invoke-Expression\n" +
        "Register-ArgumentCompleter -Native -CommandName snaply -ScriptBlock {\n" +
        "  param($wordToComplete, $commandAst, $cursorPosition)\n" +
        $"  @('{string.Join("','", Commands.Split(' '))}','--json','--quiet','--verbose','--no-color','--help','--version') |\n" +
        "    Where-Object { $_ -like \"$wordToComplete*\" } |\n" +
        "    ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }\n" +
        "}";

    private static string Fish =>
        "# snaply fish completion — save to ~/.config/fish/completions/snaply.fish\n" +
        $"complete -c snaply -f -a \"{Commands}\"";
}
