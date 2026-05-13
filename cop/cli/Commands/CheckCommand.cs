using System.CommandLine;
using Cop.Lang;
using Cop.Providers;

namespace Cop.Cli.Commands;

/// <summary>
/// Backward-compat alias: 'cop check' redirects to 'cop run' package mode.
/// </summary>
public static class CheckCommand
{
    public static Command Create()
    {
        var packagesArg = new Argument<string[]>("packages")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "One or more package names to run (e.g., csharp-style fdg)"
        };
        var targetOption = new Option<string>("-t") { Description = "Target directory to analyze (default: current directory)" };
        var rulesOption = new Option<string>("-c") { Description = "Comma-separated list of specific rules to run (default: all)" };
        var formatOption = new Option<string>("-f") { Description = "Output format: text (default) or json" };
        formatOption.DefaultValueFactory = _ => "text";
        var diagOption = new Option<bool>("-d") { Description = "Print diagnostic timing to stderr" };
        var command = new Command("check", "Run analysis packages (alias for 'cop run <packages>')")
        {
            packagesArg,
            targetOption,
            rulesOption,
            formatOption,
            diagOption
        };
        command.Hidden = true;
        command.SetAction(parseResult =>
        {
            var packages = parseResult.GetValue(packagesArg)!;
            var target = parseResult.GetValue(targetOption);
            var rules = parseResult.GetValue(rulesOption);
            var format = parseResult.GetValue(formatOption);
            var isDiag = parseResult.GetValue(diagOption);

            string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();
            string[]? rulesFilter = null;
            if (!string.IsNullOrEmpty(rules))
                rulesFilter = rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return RunCommand.ExecutePackages(packages, rootPath, rulesFilter, format, isDiag);
        });
        return command;
    }
}
