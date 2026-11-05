// Runs analysis packages against a target directory using the check command.

using System.CommandLine;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Parser;
using Cop.Providers;

namespace Cop.Cli.Commands;

/// <summary>
/// Runs check packages against the target directory.
/// </summary>
public static class CheckCommand
{
    /// <summary>
    /// Creates the package-check command and its target and output options.
    /// </summary>
    public static Command Create()
    {
        Argument<string[]> packagesArg = new Argument<string[]>("packages")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "One or more package names to run (e.g., csharp-checks json-checks)"
        };
        Option<string> targetOption = new Option<string>("-t") { Description = "Target directory to analyze (default: current directory)" };
        Option<string> rulesOption = new Option<string>("-c") { Description = "Comma-separated list of specific checks to run (default: all)" };
        Option<string> formatOption = new Option<string>("-f") { Description = "Output format: text (default) or json" };
        formatOption.DefaultValueFactory = _ => "text";
        Option<bool> diagOption = new Option<bool>("-d") { Description = "Print diagnostic timing to stderr" };
        Command command = new Command("check", "Run analysis packages against the target directory")
        {
            packagesArg,
            targetOption,
            rulesOption,
            formatOption,
            diagOption
        };
        command.SetAction(parseResult =>
        {
            string[] packages = parseResult.GetValue(packagesArg)!;
            string? target = parseResult.GetValue(targetOption);
            string? rules = parseResult.GetValue(rulesOption);
            string? format = parseResult.GetValue(formatOption);
            bool isDiag = parseResult.GetValue(diagOption);

            string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();
            string[]? rulesFilter = null;
            if (!string.IsNullOrEmpty(rules))
            {
                rulesFilter = rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

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
        string cwd = Directory.GetCurrentDirectory();
        string[] copFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);

        if (copFiles.Length == 0)
        {
            PrintGettingStarted();
            return 0;
        }

        // Parse .cop files to understand what they contain
        List<string> imports = new List<string>();
        bool hasOwnLogic = false;

        foreach (string file in copFiles)
        {
            try
            {
                string source = File.ReadAllText(file);
                ModuleNode module = CopParser.Parse(source, file);

                foreach (Declaration decl in module.Declarations)
                {
                    if (decl is ImportDecl imp)
                    {
                        imports.Add(imp.ModuleName);
                    }
                    else if (decl is FunctionDecl || decl is LetDecl || decl is CommandDecl)
                    {
                        hasOwnLogic = true;
                    }
                }
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
