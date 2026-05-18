using System.CommandLine;
using Cop.Lang;
using Cop.Providers;

namespace Cop.Cli.Commands;

/// <summary>
/// Runs check packages against the target directory.
/// </summary>
public static class CheckCommand
{
    public static Command Create()
    {
        var packagesArg = new Argument<string[]>("packages")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "One or more package names to run (e.g., csharp-checks json-checks)"
        };
        var targetOption = new Option<string>("-t") { Description = "Target directory to analyze (default: current directory)" };
        var rulesOption = new Option<string>("-c") { Description = "Comma-separated list of specific checks to run (default: all)" };
        var formatOption = new Option<string>("-f") { Description = "Output format: text (default) or json" };
        formatOption.DefaultValueFactory = _ => "text";
        var diagOption = new Option<bool>("-d") { Description = "Print diagnostic timing to stderr" };
        var command = new Command("check", "Run analysis packages against the target directory")
        {
            packagesArg,
            targetOption,
            rulesOption,
            formatOption,
            diagOption
        };
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

    /// <summary>
    /// Runs checks based on local .cop files in the current directory.
    /// If .cop files define commands or checks (have export lets / commands), run as a program.
    /// If .cop files only import packages without defining logic, run those packages as checks.
    /// Otherwise, prints a getting-started message.
    /// </summary>
    public static int ExecuteFromConfig()
    {
        var cwd = Directory.GetCurrentDirectory();
        var copFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);

        if (copFiles.Length == 0)
        {
            PrintGettingStarted();
            return 0;
        }

        // Parse .cop files to understand what they contain
        var scriptFiles = new List<ScriptFile>();
        var imports = new List<string>();
        bool hasOwnLogic = false;

        foreach (var file in copFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                var sf = ScriptParser.Parse(source, file);
                scriptFiles.Add(sf);
                imports.AddRange(sf.Imports);

                // Does this file define its own commands, checks, or predicates?
                if (sf.Commands.Count > 0 || sf.LetDeclarations.Count > 0 || sf.Predicates.Count > 0)
                    hasOwnLogic = true;
            }
            catch { /* skip unparseable files */ }
        }

        if (hasOwnLogic)
        {
            // This is a cop program — run it with general-purpose semantics
            return RunCommand.Execute(null);
        }

        if (imports.Count > 0)
        {
            // Config-only file: just imports, run as check packages
            return RunCommand.ExecutePackages(imports.ToArray(), cwd, null, "text", false);
        }

        PrintGettingStarted();
        return 0;
    }

    private static void PrintGettingStarted()
    {
        Console.WriteLine("cop — a DSL for processing lists");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  cop <packages>         Run check packages (e.g., cop csharp-checks)");
        Console.WriteLine("  cop run [<command>]    Run .cop programs");
        Console.WriteLine("  cop list <package>     List checks in a package");
        Console.WriteLine("  cop repl              Launch interactive REPL");
        Console.WriteLine();
        Console.WriteLine("Getting started:");
        Console.WriteLine("  1. Run checks:  cop csharp-checks");
        Console.WriteLine("  2. Customize:   Create a .cop file with 'import csharp-checks'");
        Console.WriteLine("                  then just run 'cop' with no arguments");
        Console.WriteLine();
        Console.WriteLine("  cop -h for more options");
    }
}
