using System.CommandLine;
using Cop.Lang;
using Cop.Providers;

namespace Cop.Cli.Commands;

public static class CheckCommand
{
    public static Command Create()
    {
        var packagesArg = new Argument<string[]>("packages")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "One or more package names to run (e.g., csharp-style csharp-library)"
        };
        var targetOption = new Option<string>("-t") { Description = "Target directory to analyze (default: current directory)" };
        var rulesOption = new Option<string>("-c") { Description = "Comma-separated list of specific rules to run (default: all)" };
        var formatOption = new Option<string>("-f") { Description = "Output format: text (default) or json" };
        formatOption.DefaultValueFactory = _ => "text";
        var diagOption = new Option<bool>("-d") { Description = "Print diagnostic timing to stderr" };
        var command = new Command("check", "Run analysis checks from packages against your code")
        {
            packagesArg,
            targetOption,
            rulesOption,
            formatOption,
            diagOption
        };
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(packagesArg)!,
            parseResult.GetValue(targetOption),
            parseResult.GetValue(rulesOption),
            parseResult.GetValue(formatOption),
            parseResult.GetValue(diagOption)));
        return command;
    }

    public static int Execute(string[] packages, string? target = null, string? rules = null, string? format = null, bool diag = false)
    {
        string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();

        // Discover feed paths from cwd (walking up to find packages/ dirs)
        var feedPaths = FindFeedPaths(rootPath);

        // Also add default feed's restored packages (in user profile)
        var restorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "packages");
        if (Directory.Exists(restorePath))
            feedPaths.Add(restorePath);

        if (feedPaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No package feeds found. Ensure a 'packages/' directory exists in your project tree or run 'cop package restore'.");
            return 2;
        }

        // Parse rules filter
        var rulesList = new List<string>();
        if (!string.IsNullOrEmpty(rules))
            rulesList.AddRange(rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var result = Engine.RunProject(feedPaths, [.. packages], rootPath, rulesList);

        // Output results
        if (result.HasFatalErrors)
        {
            foreach (var error in result.Errors)
                Console.Error.WriteLine(error);
            return 2;
        }

        foreach (var error in result.ParseErrors)
            Console.Error.WriteLine(error);

        if (result.Outputs.Count == 0)
        {
            Console.WriteLine("  All checks passed.");
            return 0;
        }

        foreach (var output in result.Outputs)
            Console.WriteLine(AnsiRenderer.Render(output.Content));

        return 1;
    }

    private static List<string> FindFeedPaths(string startDir)
    {
        var paths = new List<string>();
        var dir = startDir;
        while (dir is not null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
                paths.Add(packagesDir);
            dir = Path.GetDirectoryName(dir);
        }
        return paths;
    }
}
