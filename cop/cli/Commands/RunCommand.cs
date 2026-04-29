using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;
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
            Description = "Command name, .cop file, or HTTPS URL to run"
        };
        var extraArgsArg = new Argument<string[]>("args")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Extra arguments passed to the program"
        };
        var targetOption = new Option<string>("-t") { Description = "Target: directory, file, or comma-separated file list to pass to the program (default: current directory)" };
        var formatOption = new Option<string>("-f") { Description = "Output format: text (default) or json" };
        formatOption.DefaultValueFactory = _ => "text";
        var commandsOption = new Option<string>("-c") { Description = "Comma-separated list of commands to run (default: all)" };
        var diagOption = new Option<bool>("-d") { Description = "Print diagnostic timing for each engine phase to stderr" };
        var command = new Command("run", "Run .cop programs")
        {
            commandArg,
            extraArgsArg,
            targetOption,
            formatOption,
            commandsOption,
            diagOption
        };
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(commandArg),
            parseResult.GetValue(extraArgsArg),
            parseResult.GetValue(targetOption),
            parseResult.GetValue(formatOption),
            parseResult.GetValue(commandsOption),
            parseResult.GetValue(diagOption)));
        return command;
    }

    public static int Execute(string? command, string[]? programArgs = null, string? target = null, string? format = null, string? commands = null, bool diag = false)
    {
        if (command != null && IsUri(command))
            return ExecuteFromUri(command, programArgs, target, format, commands, diag);

        string? commandName = null;
        string scriptsDir;
        string rootPath;

        if (command != null && command.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            // .cop file mode: load scripts from that file's directory
            var spec = new FileInfo(command);
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
            // Command name mode: discover .cop files in cwd
            scriptsDir = Directory.GetCurrentDirectory();
            rootPath = scriptsDir;
            commandName = command;
        }

        // Override rootPath if -t is specified
        if (!string.IsNullOrEmpty(target))
        {
            rootPath = Path.GetFullPath(target);
        }

        // Parse -c filter
        string[]? commandFilter = null;
        if (!string.IsNullOrEmpty(commands))
            commandFilter = commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(msg) : null;
        var result = Engine.Run(scriptsDir, rootPath, commandName, programArgs, commandFilter, diagLog);

        return HandleResult(result, format, rootPath);
    }

    private static int ExecuteFromUri(string uri, string[]? programArgs, string? target, string? format, string? commands, bool diag)
    {
        if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Error: Only HTTPS URLs are supported for remote .cop files");
            return 1;
        }

        string? tempDir = null;
        try
        {
            // Download the .cop file
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "cop-cli");

            var response = httpClient.GetAsync(uri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error: Failed to download '{uri}' (HTTP {(int)response.StatusCode})");
                return 1;
            }

            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            // Save to temp directory with .cop extension
            tempDir = Path.Combine(Path.GetTempPath(), $"cop-remote-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, "remote.cop");
            File.WriteAllText(tempFile, content);

            // scriptsDir = temp dir, rootPath = CWD (or -t override)
            var scriptsDir = tempDir;
            var rootPath = !string.IsNullOrEmpty(target)
                ? Path.GetFullPath(target)
                : Directory.GetCurrentDirectory();

            // Parse command name from extra args
            string? commandName = null;
            if (programArgs is { Length: > 0 } && !programArgs[0].StartsWith('/') && !programArgs[0].StartsWith('-'))
            {
                commandName = programArgs[0];
                programArgs = programArgs[1..];
            }

            // Parse -c filter
            string[]? commandFilter = null;
            if (!string.IsNullOrEmpty(commands))
                commandFilter = commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Pass CWD feed paths so locally-restored packages can resolve
            var additionalFeedPaths = FindFeedPathsFromCwd();

            Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(msg) : null;
            var result = Engine.Run(scriptsDir, rootPath, commandName, programArgs, commandFilter, diagLog, additionalFeedPaths: additionalFeedPaths);

            return HandleResult(result, format, rootPath);
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Error: Failed to download '{uri}': {ex.Message}");
            return 1;
        }
        finally
        {
            if (tempDir is not null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private static bool IsUri(string value)
        => value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string[] FindFeedPathsFromCwd()
    {
        var paths = new List<string>();
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
                paths.Add(packagesDir);
            dir = Path.GetDirectoryName(dir);
        }
        return paths.ToArray();
    }

    private static int HandleResult(EngineResult result, string? format, string rootPath)
    {
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
