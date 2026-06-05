using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;
using System.Security;
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
        var cqlOption = new Option<bool>("-cql") { Description = "Transpile .cop checks to CodeQL .ql files instead of running them" };
        var command = new Command("run", "Run .cop programs")
        {
            commandArg,
            extraArgsArg,
            targetOption,
            formatOption,
            commandsOption,
            diagOption,
            cqlOption
        };
        command.SetAction(parseResult =>
        {
            if (parseResult.GetValue(cqlOption))
                return ExecuteCodeQL(parseResult.GetValue(commandArg));
            return Execute(
                parseResult.GetValue(commandArg),
                parseResult.GetValue(extraArgsArg),
                parseResult.GetValue(targetOption),
                parseResult.GetValue(formatOption),
                parseResult.GetValue(commandsOption),
                parseResult.GetValue(diagOption));
        });
        return command;
    }

    /// <summary>
    /// Transpiles .cop files to CodeQL .ql files instead of executing them.
    /// </summary>
    public static int ExecuteCodeQL(string? copFileArg)
    {
        string scriptsDir;
        if (copFileArg != null && copFileArg.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            var spec = new FileInfo(copFileArg);
            if (!spec.Exists)
            {
                Console.Error.WriteLine($"Error: File '{spec.FullName}' not found");
                return 1;
            }
            scriptsDir = spec.DirectoryName ?? Directory.GetCurrentDirectory();
        }
        else
        {
            scriptsDir = Directory.GetCurrentDirectory();
        }

        // Parse all .cop files in the directory
        var scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        if (scriptFilePaths.Length == 0)
        {
            Console.Error.WriteLine("Error: No .cop files found");
            return 1;
        }

        var scriptFiles = new List<ScriptFile>();
        var parseErrors = new List<string>();

        foreach (var path in scriptFilePaths)
        {
            try
            {
                var source = File.ReadAllText(path);
                scriptFiles.Add(Cop.Lang.Parser.CopParser.ParseFile(source, path));
            }
            catch (ParseException ex)
            {
                parseErrors.Add(ex.Message);
            }
        }

        if (parseErrors.Count > 0)
        {
            foreach (var error in parseErrors)
                Console.Error.WriteLine(error);
            return 2;
        }

        // Resolve imports to find imported script files
        var feedPaths = FindFeedPathsFromCwd();
        var importedFiles = new List<ScriptFile>();
        foreach (var sf in scriptFiles)
        {
            foreach (var imp in sf.Imports)
            {
                foreach (var feed in feedPaths)
                {
                    var pkgDir = ImportResolver.FindPackageDir(feed, imp);
                    if (pkgDir is not null)
                    {
                        var impFiles = Directory.GetFiles(pkgDir, "*.cop", SearchOption.AllDirectories);
                        foreach (var impFile in impFiles)
                        {
                            try
                            {
                                var source = File.ReadAllText(impFile);
                                importedFiles.Add(Cop.Lang.Parser.CopParser.ParseFile(source, impFile));
                            }
                            catch { /* skip unparseable import files */ }
                        }
                        break;
                    }
                }
            }
        }

        // Transpile each script file
        int totalFiles = 0;
        bool hasErrors = false;

        foreach (var sf in scriptFiles)
        {
            var transpiler = new CqlTranspiler(sf, importedFiles);
            var result = transpiler.Transpile();

            if (result.HasErrors)
            {
                foreach (var error in result.Errors)
                    Console.Error.WriteLine(error);
                hasErrors = true;
                continue;
            }

            if (result.Files.Count == 0)
                continue;

            // Write .ql files to codeql/ subdirectory next to the .cop file
            var copDir = Path.GetDirectoryName(sf.FilePath) ?? scriptsDir;
            var cqlDir = Path.Combine(copDir, "codeql");
            Directory.CreateDirectory(cqlDir);

            foreach (var qlFile in result.Files)
            {
                var outPath = Path.Combine(cqlDir, qlFile.FileName);
                File.WriteAllText(outPath, qlFile.Content);
                Console.WriteLine($"  Generated: {Path.GetRelativePath(scriptsDir, outPath)}");
                totalFiles++;
            }
        }

        if (hasErrors) return 2;

        if (totalFiles == 0)
        {
            Console.WriteLine("CodeQL: 0 query file(s) generated.");
            Console.WriteLine("  Hint: Only Code provider collections (Types, Statements, Calls, Methods) can be transpiled to CodeQL.");
            Console.WriteLine("  Hint: Collections like Lines, Files, and Api have no CodeQL equivalent.");
        }
        else
        {
            Console.WriteLine($"CodeQL: {totalFiles} query file(s) generated.");
        }
        return 0;
    }

    public static int Execute(string? command, string[]? programArgs = null, string? target = null, string? format = null, string? commands = null, bool diag = false, bool onlyIfModified = false)
    {
        if (command != null && IsUri(command))
            return ExecuteFromUri(command, programArgs, target, format, commands, diag);

        // -om: skip analysis if no files modified in git working tree
        if (onlyIfModified && !HasModifiedFiles(target ?? Directory.GetCurrentDirectory()))
            return 0;

        string? commandName = null;
        string scriptsDir;
        string rootPath;

        string? scopeToFile = null;

        if (command != null && command.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            // .cop file mode: use file's directory as scriptsDir, load all .cop files there
            var spec = new FileInfo(command);
            if (!spec.Exists) { Console.Error.WriteLine($"Error: File '{spec.FullName}' not found"); return 1; }
            scriptsDir = spec.DirectoryName ?? Directory.GetCurrentDirectory();
            rootPath = scriptsDir;
            scopeToFile = spec.FullName;

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

        // When a specific .cop file is named, scope commands to that file only
        if (scopeToFile != null && commandFilter == null && commandName == null)
        {
            var fileCommands = GetCommandNamesFromFile(scopeToFile);
            if (fileCommands.Length > 0)
                commandFilter = fileCommands;
            else
            {
                Console.Error.WriteLine($"Error: No commands defined in '{command}'");
                return 1;
            }
        }

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
        catch (NotImplementedException)
        {
            // Streaming not yet reimplemented — fall through to normal execution
        }

        var result = Engine.Run(scriptsDir, rootPath, commandName, programArgs, commandFilter, diagLog, additionalFeedPaths: FindFeedPathsFromCwd());

        return HandleResult(result, format, rootPath);
    }

    /// <summary>
    /// Runs named packages against the target directory (merged from cop check).
    /// Packages are auto-restored from configured GitHub feeds if not found locally.
    /// </summary>
    public static int ExecutePackages(string[] packages, string? target = null, string[]? rules = null, string? format = null, bool diag = false, bool onlyIfModified = false)
    {
        string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();

        // -om: skip analysis if no files modified in git working tree
        if (onlyIfModified && !HasModifiedFiles(rootPath))
            return 0;

        Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(ColorDiagLine(msg)) : null;

        // Discover feed paths from both rootPath and CWD (includes global cache)
        var feedPaths = PackageResolver.GetFeedPaths(rootPath);
        var cwd = Directory.GetCurrentDirectory();
        if (!string.Equals(Path.GetFullPath(cwd), Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in PackageResolver.GetFeedPaths(cwd))
            {
                if (!feedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    feedPaths.Add(p);
            }
        }

        // Auto-restore any packages not found locally from configured GitHub feeds
        var cachePath = PackageResolver.GlobalCachePath;
        var missing = FindMissingPackages(packages, feedPaths, cachePath);
        if (missing.Count > 0)
        {
            var restored = AutoRestorePackagesAsync(missing, cachePath).GetAwaiter().GetResult();
            if (!restored)
                return 2;
        }

        // Ensure cache path is in feed list after restore
        if (Directory.Exists(cachePath) && !feedPaths.Contains(cachePath))
            feedPaths.Add(cachePath);

        if (feedPaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No package feeds found.");
            return 2;
        }

        // Convert rules filter
        var rulesList = rules?.ToList() ?? [];

        var result = Engine.RunProject(feedPaths, [.. packages], rootPath, rulesList, diagLog: diagLog);

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

    private static string[] FindFeedPathsFromCwd() =>
        PackageResolver.GetFeedPaths().ToArray();

    private static int HandleResult(EngineResult result, string? format, string rootPath)
    {
        // Use structured diagnostics for rich error output when available
        if (result.Diagnostics is { Count: > 0 })
        {
            DiagnosticFormatter.WriteAllToStdErr(result.Diagnostics);
        }

        // Fall back to string errors for any not covered by structured diagnostics
        if (result.Diagnostics is null or { Count: 0 })
        {
            foreach (var error in result.ParseErrors)
                Console.Error.WriteLine(error);
        }

        if (result.Warnings is { Count: > 0 })
        {
            foreach (var warning in result.Warnings)
                Console.Error.WriteLine(warning);
        }

        if (result.HasFatalErrors)
        {
            // Only print string errors that aren't already represented by diagnostics
            if (result.Diagnostics is null or { Count: 0 })
            {
                foreach (var error in result.Errors)
                    Console.Error.WriteLine(error);
            }
            else
            {
                // Print non-diagnostic errors (e.g., runtime errors without source info)
                foreach (var error in result.Errors)
                {
                    if (!result.Diagnostics.Any(d => error.Contains(d.Message)))
                        Console.Error.WriteLine(error);
                }
            }
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

        // Exit non-zero when output was produced (violations found)
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
    /// Extracts command names defined in a specific .cop file (for single-file scoping).
    /// Commands are desugared to uppercase FunctionDecl by the parser.
    /// </summary>
    private static string[] GetCommandNamesFromFile(string filePath)
    {
        try
        {
            var source = File.ReadAllText(filePath);
            var module = Cop.Lang.Parser.CopParser.Parse(source, filePath);
            return module.Declarations
                .OfType<Cop.Lang.Ast.FunctionDecl>()
                .Where(f => f.Name.Length > 0 && f.Name == f.Name.ToUpperInvariant())
                .Select(c => c.Name)
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error parsing '{filePath}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Checks if the working tree has modified files using git status.
    /// Returns true if files are modified or if git is unavailable (safe fallback).
    /// </summary>
    private static bool HasModifiedFiles(string workingDir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return true;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0) return true;
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return true; // git not available → assume modified
        }
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
                var sf = Cop.Lang.Parser.CopParser.ParseFile(source, path);
                foreach (var imp in sf.Imports)
                    imports.Add(imp);
            }
            catch { /* skip unparseable files */ }
        }

        if (imports.Count == 0) return;

        // Determine available feed paths (includes global cache + walk-up from cwd and scriptsDir)
        var feedPaths = PackageResolver.GetFeedPaths();
        // Also include walk-up from scriptsDir if different from cwd
        foreach (var p in PackageResolver.GetFeedPaths(scriptsDir))
        {
            if (!feedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                feedPaths.Add(p);
        }

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

        string? githubToken = ResolveGitHubToken();
        using var httpClient = new HttpClient();
        var source2 = new GitHubPackageSource(httpClient, githubToken);

        Directory.CreateDirectory(cachePath);

        // BFS: download missing packages and their transitive imports
        var queue = new Queue<string>(missing);
        var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (missing.Count > 0)
        {
            var feedCount = githubFeeds.Distinct().Count();
            Console.Error.WriteLine($"Auto-restoring {missing.Count} package(s) from {feedCount} feed(s)...");
        }

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
                    Console.Error.Write($"  Restoring '{pkgName}' from {feed}...");
                    var files = source2.DownloadPackageFilesAsync(pkgRef).GetAwaiter().GetResult();

                    if (files.Count == 0) { Console.Error.WriteLine(" no files"); continue; }

                    var pkgDir = Path.Combine(cachePath, pkgName);
                    foreach (var (relativePath, content) in files)
                    {
                        var destPath = ValidatePackagePath(relativePath, pkgDir);
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
                            var sf = Cop.Lang.Parser.CopParser.ParseFile(src, filePath);
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

        if (downloaded.Count > 0)
        {
            Console.Error.WriteLine($"{downloaded.Count} package(s) restored successfully");
        }
    }

    /// <summary>
    /// Returns package names that cannot be found in any local feed path or cache.
    /// </summary>
    private static List<string> FindMissingPackages(string[] packages, List<string> feedPaths, string cachePath)
    {
        var allPaths = new List<string>(feedPaths);
        if (Directory.Exists(cachePath))
            allPaths.Add(cachePath);

        var missing = new List<string>();
        foreach (var pkg in packages)
        {
            bool found = false;
            foreach (var feed in allPaths)
            {
                if (ImportResolver.FindPackageDir(Path.GetFullPath(feed), pkg) is not null)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                missing.Add(pkg);
        }
        return missing;
    }

    /// <summary>
    /// Downloads missing packages from configured GitHub feeds into the cache directory.
    /// Recursively resolves imports from downloaded .cop files.
    /// </summary>
    public static async Task<bool> AutoRestorePackagesAsync(List<string> packageNames, string cachePath)
    {
        var feedManager = new FeedManager();
        var feeds = feedManager.GetFeeds();

        var githubFeeds = feeds
            .Where(f => f.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (githubFeeds.Count == 0)
        {
            Console.Error.WriteLine("Error: No GitHub feeds configured. Run 'cop feed add github.com/owner/repo'.");
            return false;
        }

        string? githubToken = ResolveGitHubToken();
        using var httpClient = new HttpClient();
        var source = new GitHubPackageSource(httpClient, githubToken);

        Directory.CreateDirectory(cachePath);

        // BFS: download requested packages, then their imports
        var queue = new Queue<string>(packageNames);
        var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (packageNames.Count > 0)
        {
            var feedCount = githubFeeds.Distinct().Count();
            Console.Error.WriteLine($"Auto-restoring {packageNames.Count} package(s) from {feedCount} feed(s)...");
        }

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
                    Console.Error.Write($"  Restoring '{pkgName}' from {feed}...");
                    var files = await source.DownloadPackageFilesAsync(pkgRef);

                    if (files.Count == 0) { Console.Error.WriteLine(" no files"); continue; }

                    var pkgDir = Path.Combine(cachePath, pkgName);
                    foreach (var (relativePath, content) in files)
                    {
                        var destPath = ValidatePackagePath(relativePath, pkgDir);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        await File.WriteAllBytesAsync(destPath, content);
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
                            var sf = Cop.Lang.Parser.CopParser.ParseFile(src, filePath);
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
            {
                Console.Error.WriteLine($"Error: Package '{pkgName}' not found in any configured feed.");
                return false;
            }
        }

        if (downloaded.Count > 0)
        {
            Console.Error.WriteLine($"{downloaded.Count} package(s) restored successfully");
        }

        return true;
    }

    internal static string? ResolveGitHubToken()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) return token;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    return output;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Validates that a relative path from a package doesn't escape the intended directory.
    /// Prevents path traversal attacks via sequences like ../../ in package file paths.
    /// </summary>
    private static string ValidatePackagePath(string relativePath, string baseDir)
    {
        // Normalize the path by combining with a dummy base and checking it stays within bounds
        var testBase = Path.GetFullPath("dummy");
        var testPath = Path.GetFullPath(Path.Combine("dummy", relativePath));
        
        if (!testPath.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !testPath.Equals(testBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Invalid package path (potential path traversal): {relativePath}");
        }
        
        // Return the validated combined path
        return Path.Combine(baseDir, relativePath);
    }
}
