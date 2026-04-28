using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Cop.Lang;
using Cop.Providers;

namespace Cop.Cli.Commands;

public static class RunCommand
{
    public static Command Create()
    {
        var commandArg = new Argument<string>("command")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Command name or .cop file path"
        };
        var extraArgsArg = new Argument<string[]>("args")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Extra arguments passed to the program"
        };
        var formatOption = new Option<string>("--format") { Description = "Output format: text (default) or json" };
        formatOption.DefaultValueFactory = _ => "text";
        var commandsOption = new Option<string>("--commands") { Description = "Comma-separated list of check names to run (default: all)" };
        var diagOption = new Option<bool>("--diag") { Description = "Print diagnostic timing for each engine phase to stderr" };
        var command = new Command("run", "Run .cop programs")
        {
            commandArg,
            extraArgsArg,
            formatOption,
            commandsOption,
            diagOption
        };
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(commandArg),
            parseResult.GetValue(extraArgsArg),
            parseResult.GetValue(formatOption),
            parseResult.GetValue(commandsOption),
            parseResult.GetValue(diagOption)));
        return command;
    }

    public static int Execute(string? commandOrFile, string[]? programArgs = null, string? format = null, string? commands = null, bool diag = false)
    {
        string? commandName = null;
        string scriptsDir;
        string rootPath;

        if (commandOrFile != null && commandOrFile.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            // File path mode: treat as file path
            var spec = new FileInfo(commandOrFile);
            if (!spec.Exists) { Console.Error.WriteLine($"Error: File '{spec.FullName}' not found"); return 1; }
            scriptsDir = spec.DirectoryName ?? Directory.GetCurrentDirectory();
            rootPath = scriptsDir;

            // First extra arg is the command name (if not a switch)
            if (programArgs is { Length: > 0 } && !programArgs[0].StartsWith('/') && !programArgs[0].StartsWith('-'))
            {
                commandName = programArgs[0];
                programArgs = programArgs[1..];
            }
        }
        else
        {
            // Directory discovery mode
            scriptsDir = Directory.GetCurrentDirectory();
            rootPath = scriptsDir;
            commandName = commandOrFile;
        }

        // Parse --commands filter
        string[]? commandFilter = null;
        if (!string.IsNullOrEmpty(commands))
            commandFilter = commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(msg) : null;
        var result = Engine.Run(scriptsDir, rootPath, commandName, programArgs, commandFilter, diagLog);

        foreach (var error in result.ParseErrors)
            Console.Error.WriteLine(error);

        if (result.Warnings is { Count: > 0 })
        {
            foreach (var warning in result.Warnings)
                Console.Error.WriteLine(warning);
        }

        if (result.HasFatalErrors)
        {
            foreach (var error in result.Errors)
                Console.Error.WriteLine(error);
            return 2;
        }

        bool isJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

        if (isJson)
        {
            WriteOutputsAsJson(result.Outputs);
        }
        else
        {
            foreach (var output in result.Outputs)
                Console.WriteLine(AnsiRenderer.Render(output.Content));
        }

        // Write SAVE command outputs to files (paths are relative to codebase)
        if (result.FileOutputs is { Count: > 0 })
        {
            foreach (var output in result.FileOutputs)
            {
                var filePath = Path.IsPathRooted(output.Path)
                    ? null  // reject absolute paths
                    : Path.GetFullPath(Path.Combine(rootPath, output.Path));

                if (filePath is null || !filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"SAVE error: path '{output.Path}' is outside the project root");
                    continue;
                }

                var dir = Path.GetDirectoryName(filePath);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(filePath, output.Content);
                Console.WriteLine($"SAVE: {output.Path}");
            }
        }

        // Command mode: output is informational, exit 0
        if (result.IsCommandMode)
            return 0;

        return result.Outputs.Count > 0 || result.HasParseErrors ? 1 : 0;
    }

    private static void WriteOutputsAsJson(List<PrintOutput> outputs)
    {
        var items = outputs.Select(o => new { message = o.Message });
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
    }
}
