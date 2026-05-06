using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;
using System.Text.Json;
using Cop.Core;
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

        Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(ColorDiagLine(msg)) : null;

        // Auto-restore missing imports before execution
        AutoRestoreImports(scriptsDir, diagLog);

        // Try streaming mode (auto-detect or by command name)
        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            Engine.RunStreamingAsync(scriptsDir, commandName, cts.Token, diagLog, additionalFeedPaths: FindFeedPathsFromCwd()).GetAwaiter().GetResult();
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            // No streaming command or setup failed — fall through to normal execution
            diagLog?.Invoke($"[diag] Streaming mode skipped: {ex.Message}");
        }

        var result = Engine.Run(scriptsDir, rootPath, commandName, programArgs, commandFilter, diagLog, additionalFeedPaths: FindFeedPathsFromCwd());

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

            Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(ColorDiagLine(msg)) : null;
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

        // Include ~/.cop/packages/ (auto-restored packages)
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop", "packages");
        if (Directory.Exists(cachePath))
            paths.Add(cachePath);

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

    /// <summary>
    /// Apply ANSI colors to diagnostic lines: [diag] in gray, [trace] in cyan, [debug] in magenta.
    /// </summary>
    internal static string ColorDiagLine(string msg)
    {
        const string gray = "\x1b[90m";
        const string cyan = "\x1b[36m";
        const string magenta = "\x1b[35m";
        const string reset = "\x1b[0m";

        if (msg.StartsWith("[trace]"))
            return $"{cyan}{msg}{reset}";
        if (msg.StartsWith("[debug]"))
            return $"{magenta}{msg}{reset}";
        // [diag] and everything else
        return $"{gray}{msg}{reset}";
    }

    /// <summary>
    /// Parses .cop files in the scripts directory, discovers imports, and auto-restores
    /// any missing packages from configured GitHub feeds into ~/.cop/packages/.
    /// </summary>
    private static void AutoRestoreImports(string scriptsDir, Action<string>? diagLog)
    {
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop", "packages");

        // Parse all .cop files to collect imports
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        foreach (var path in scriptFilePaths)
        {
            try
            {
                var source = File.ReadAllText(path);
                var sf = ScriptParser.Parse(source, path);
                foreach (var imp in sf.Imports)
                    imports.Add(imp);
            }
            catch { /* skip unparseable files */ }
        }

        if (imports.Count == 0) return;

        // Determine available feed paths (same as FindFeedPathsFromCwd logic)
        var feedPaths = FindFeedPathsFromCwd();

        // Find imports that can't be resolved from any known path
        var missing = new List<string>();
        foreach (var imp in imports)
        {
            bool found = false;
            foreach (var feed in feedPaths)
            {
                if (ImportResolver.FindPackageDir(feed, imp) is not null)
                {
                    found = true;
                    break;
                }
            }
            // Also check scriptsDir walk-up paths (Engine's FindFeedPaths)
            if (!found)
            {
                var dir = scriptsDir;
                while (dir is not null)
                {
                    var packagesDir = Path.Combine(dir, "packages");
                    if (Directory.Exists(packagesDir) && ImportResolver.FindPackageDir(packagesDir, imp) is not null)
                    {
                        found = true;
                        break;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            if (!found)
                missing.Add(imp);
        }

        if (missing.Count == 0) return;

        // Try to restore from GitHub feeds
        var feedManager = new FeedManager();
        var feeds = feedManager.GetFeeds();
        var githubFeeds = feeds
            .Where(f => f.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (githubFeeds.Count == 0)
        {
            diagLog?.Invoke($"[diag] Missing imports ({string.Join(", ", missing)}) but no GitHub feeds configured");
            return;
        }

        string? githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        using var httpClient = new HttpClient();
        var source2 = new GitHubPackageSource(httpClient, githubToken);

        Directory.CreateDirectory(cachePath);

        // BFS: download missing packages and their transitive imports
        var queue = new Queue<string>(missing);
        var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var pkgName = queue.Dequeue();
            if (downloaded.Contains(pkgName)) continue;

            if (ImportResolver.FindPackageDir(cachePath, pkgName) is not null)
            {
                downloaded.Add(pkgName);
                continue;
            }

            bool restored = false;
            foreach (var feed in githubFeeds)
            {
                try
                {
                    var pkgRef = PackageReference.Parse($"{feed}/{pkgName}");
                    Console.Error.Write($"  Restoring {pkgName}...");
                    var files = source2.DownloadPackageFilesAsync(pkgRef).GetAwaiter().GetResult();

                    if (files.Count == 0) { Console.Error.WriteLine(" no files"); continue; }

                    var pkgDir = Path.Combine(cachePath, pkgName);
                    foreach (var (relativePath, content) in files)
                    {
                        var destPath = Path.Combine(pkgDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.WriteAllBytes(destPath, content);
                    }

                    Console.Error.WriteLine(" ok");
                    downloaded.Add(pkgName);
                    restored = true;

                    // Parse .cop files to discover transitive imports
                    foreach (var (relPath, _) in files.Where(f => f.Key.EndsWith(".cop", StringComparison.OrdinalIgnoreCase)))
                    {
                        var filePath = Path.Combine(pkgDir, relPath);
                        try
                        {
                            var src = File.ReadAllText(filePath);
                            var sf = ScriptParser.Parse(src, filePath);
                            foreach (var imp in sf.Imports)
                            {
                                if (!downloaded.Contains(imp))
                                    queue.Enqueue(imp);
                            }
                        }
                        catch { /* skip unparseable files */ }
                    }
                    break;
                }
                catch (PackageNotFoundException)
                {
                    Console.Error.WriteLine(" not found");
                    continue;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($" failed: {ex.Message}");
                }
            }

            if (!restored)
                Console.Error.WriteLine($"Warning: Package '{pkgName}' not found in any configured feed.");
        }
    }
}
