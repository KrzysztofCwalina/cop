// Loads Cop programs and packages, executes commands, and reports their results.

using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using Cop.Core;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Providers;

namespace Cop.Cli.Commands;

/// <summary>Executes source programs or packages against a selected target.</summary>
public static class RunCommand
{

    /// <summary>
    /// Transpiles .cop files to CodeQL .ql files instead of executing them.
    /// </summary>
    public static int ExecuteCodeQL(string? copFileArg)
    {
        string scriptsDir;
        if (copFileArg != null && copFileArg.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            FileInfo spec = new FileInfo(copFileArg);
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
        string[] scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        if (scriptFilePaths.Length == 0)
        {
            Console.Error.WriteLine("Error: No .cop files found");
            return 1;
        }

        List<ScriptFile> scriptFiles = new List<ScriptFile>();
        List<string> parseErrors = new List<string>();

        foreach (string path in scriptFilePaths)
        {
            try
            {
                string source = File.ReadAllText(path);
                scriptFiles.Add(Cop.Lang.Parser.CopParser.ParseFile(source, path));
            }
            catch (ParseException ex)
            {
                parseErrors.Add(ex.Message);
            }
        }

        if (parseErrors.Count > 0)
        {
            foreach (string error in parseErrors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        // Resolve imports to find imported script files
        string[] feedPaths = FindFeedPathsFromCwd();
        List<ScriptFile> importedFiles = new List<ScriptFile>();
        List<string> importErrors = new List<string>();
        foreach (ScriptFile sf in scriptFiles)
        {
            foreach (string imp in sf.Imports)
            {
                foreach (string feed in feedPaths)
                {
                    string? pkgDir = ImportResolver.FindPackageDir(feed, imp);
                    if (pkgDir is not null)
                    {
                        string[] impFiles = Directory.GetFiles(pkgDir, "*.cop", SearchOption.AllDirectories);
                        foreach (string impFile in impFiles)
                        {
                            try
                            {
                                string source = File.ReadAllText(impFile);
                                importedFiles.Add(Cop.Lang.Parser.CopParser.ParseFile(source, impFile));
                            }
                            catch (Exception ex) when (ex is ParseException or IOException or
                                UnauthorizedAccessException)
                            {
                                importErrors.Add($"Failed to parse import file '{impFile}': {ex.Message}");
                            }
                        }
                        break;
                    }
                }
            }
        }

        if (importErrors.Count > 0)
        {
            foreach (string error in importErrors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        // Transpile each script file
        int totalFiles = 0;
        bool hasErrors = false;

        foreach (ScriptFile sf in scriptFiles)
        {
            CqlTranspiler transpiler = new CqlTranspiler(sf, importedFiles);
            CqlTranspileResult result = transpiler.Transpile();

            if (result.HasErrors)
            {
                foreach (string error in result.Errors)
                {
                    Console.Error.WriteLine(error);
                }

                hasErrors = true;
                continue;
            }

            if (result.Files.Count == 0)
            {
                continue;
            }

            // Write .ql files to codeql/ subdirectory next to the .cop file
            string copDir = Path.GetDirectoryName(sf.FilePath) ?? scriptsDir;
            string cqlDir = Path.Combine(copDir, "codeql");
            Directory.CreateDirectory(cqlDir);

            foreach (CqlQueryFile qlFile in result.Files)
            {
                string outPath = Path.Combine(cqlDir, qlFile.FileName);
                File.WriteAllText(outPath, qlFile.Content);
                Console.WriteLine($"  Generated: {Path.GetRelativePath(scriptsDir, outPath)}");
                totalFiles++;
            }
        }

        if (hasErrors)
        {
            return 2;
        }

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

    /// <summary>Restores dependencies and executes a local program or remote script.</summary>
    public static async Task<int> ExecuteAsync(string? command, RunOptions? options = null)
    {
        options ??= new RunOptions();
        string[]? programArgs = options.Arguments?.ToArray();
        string[]? providers = options.ProviderNames?.ToArray();
        string? target = options.Target;
        string? format = options.OutputFormat;
        bool diag = options.IsDiagnosticEnabled;
        bool onlyIfModified = options.ShouldSkipUnmodifiedTargets;
        ReviewOptions? reviewOptions = options.ReviewOptions;
        Engine.IsRuleProfilingEnabled = options.IsRuleProfilingEnabled;
        try { (reviewOptions ?? new ReviewOptions()).Validate(); }
        catch (ArgumentException ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 2; }

        // -om: skip analysis if no files modified in git working tree
        if (onlyIfModified && !await HasModifiedFilesAsync(target ?? Directory.GetCurrentDirectory()))
        {
            return 0;
        }

        if (command != null && IsUri(command))
        {
            return await ExecuteFromUriAsync(command, options);
        }

        string? commandName = null;
        string scriptsDir;
        string rootPath;

        string? scopeToFile = null;

        if (command != null && command.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            // .cop file mode: use file's directory as scriptsDir, load all .cop files there
            FileInfo spec = new FileInfo(command);
            if (!spec.Exists) { Console.Error.WriteLine($"Error: File '{spec.FullName}' not found"); return 1; }
            scriptsDir = spec.DirectoryName ?? Directory.GetCurrentDirectory();
            // The analysis target defaults to the current working directory, NOT the
            // .cop file's folder, so `cop cop-checks/main.cop` run from the repo root
            // analyzes the repo root. `-t` overrides this (see ResolveTargetRoot).
            rootPath = ResolveTargetRoot(target, Directory.GetCurrentDirectory());
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
            rootPath = ResolveTargetRoot(target, scriptsDir);
            commandName = command;
        }

        // Parse -c filter
        string[]? commandFilter = options.CommandNames?.ToArray();

        // When a specific .cop file is named, scope commands to that file only
        string? implicitCommandFile = null;
        if (scopeToFile != null && commandFilter == null && commandName == null)
        {
            string[]? fileCommands = GetCommandNamesFromFile(scopeToFile);
            if (fileCommands is null)
            {
                return 2; // the file failed to parse; the parse error was already reported.
            }

            if (fileCommands.Length > 0)
            {
                // Default to the primary MAIN command when present, so a bare run does
                // NOT trigger opt-in commands (e.g. network-based AI checks, or DERIVED).
                // Those stay available explicitly via -c AI / -c DERIVED.
                string? main = fileCommands.FirstOrDefault(c => c.Equals("MAIN", StringComparison.OrdinalIgnoreCase));
                commandFilter = main is not null ? [main] : fileCommands;
            }
            else
            {
                implicitCommandFile = TryCreateImplicitMainFile(scopeToFile);
                if (implicitCommandFile is not null)
                {
                    commandFilter = ["MAIN"];
                }
                else
                {
                    Console.Error.WriteLine($"Error: No commands defined in '{command}'");
                    return 1;
                }
            }
        }

        Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(ColorDiagLine(msg)) : null;

        // Auto-restore missing imports before execution
        await AutoRestoreImportsAsync(scriptsDir, diagLog);

        // Surface imported packages' instructions to coding agents by placing their
        // instructions/*.md into the analysis target's .github/instructions/ (idempotent — a no-op
        // when the content is unchanged, so this does not churn the working tree on repeated runs).
        if (reviewOptions?.Scope != "pr")
        {
            PlaceInstructionsForImports(scriptsDir, rootPath, diagLog);
        }

        // Discover user-global check files (~/.cop/checks/) to include alongside project checks
        string[] userCheckFiles = await GetUserCheckFilesAsync(diagLog);

        // Try streaming mode (auto-detect or by command name)
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            await Engine.RunStreamingAsync(scriptsDir, commandName, cts.Token, diagLog,
                additionalFeedPaths: FindFeedPathsFromCwd());
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

        // Include user-global checks if present
        string[]? allScriptFiles = null;
        if (userCheckFiles.Length > 0)
        {
            if (scopeToFile != null)
            {
                // Single-file mode with user checks: load only the target file + user checks.
                // No need to load all sibling .cop files — they would trigger unnecessary provider loading.
                allScriptFiles = implicitCommandFile is not null
                    ? [scopeToFile, .. userCheckFiles, implicitCommandFile]
                    : [scopeToFile, .. userCheckFiles];
            }
            else
            {
                string[] projectFiles = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
                allScriptFiles = [.. projectFiles, .. userCheckFiles];
            }
            await AutoRestoreImportsAsync(UserChecksDirectory, diagLog);
        }
        else if (scopeToFile != null)
        {
            // Load all .cop files in the same directory as the specified file, including
            // subfolders. This enables the cop-checks/ pattern where main.cop imports
            // providers and other files (including those grouped in subfolders such as
            // cop-checks/csharp/derived/) define checks that use the shared environment.
            // Place the target file LAST so its command definitions take priority.
            string[] siblingFiles = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
            Array.Sort(siblingFiles, StringComparer.Ordinal);
            string[] others = siblingFiles.Where(f =>
                !string.Equals(f, scopeToFile, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f, implicitCommandFile, StringComparison.OrdinalIgnoreCase)).ToArray();
            allScriptFiles = implicitCommandFile is not null
                ? [.. others, scopeToFile, implicitCommandFile]
                : [.. others, scopeToFile];
        }

        // Resolve -p provider packages to directories
        List<(string Dir, PackageMetadata Meta)>? providerPackages = null;
        if (providers is { Length: > 0 })
        {
            providerPackages = [];
            List<string> providerFeedPaths = new List<string>(FindFeedPathsFromCwd());
            string cachePath = PackageResolver.GlobalCachePath;
            if (Directory.Exists(cachePath) && !providerFeedPaths.Contains(cachePath))
            {
                providerFeedPaths.Add(cachePath);
            }

            foreach (string providerName in providers)
            {
                string? pkgDir = null;
                foreach (string feed in providerFeedPaths)
                {
                    pkgDir = ImportResolver.FindPackageDir(feed, providerName);
                    if (pkgDir != null)
                    {
                        break;
                    }
                }
                if (pkgDir is null)
                {
                    Console.Error.WriteLine($"Error: Provider package '{providerName}' not found. Use -p with a valid package name.");
                    return 2;
                }
                PackageMetadata? metadata = PackageMetadata.LoadFromDirectoryOrDefault(pkgDir);
                if (metadata is null || !metadata.IsProvider)
                {
                    Console.Error.WriteLine($"Error: Package '{providerName}' is not a provider (no lib/ with DLL).");
                    return 2;
                }
                providerPackages.Add((pkgDir, metadata));
            }
        }

        try
        {
            EngineResult result = await Engine.RunAsync(scriptsDir, rootPath, new EngineRunOptions
            {
                CommandName = commandName,
                ProgramArgs = programArgs,
                CommandFilter = commandFilter,
                DiagnosticLog = diagLog,
                AdditionalFeedPaths = FindFeedPathsFromCwd(),
                ScriptFiles = allScriptFiles,
                ProviderPackages = providerPackages,
                ReviewOptions = reviewOptions,
            });
            return HandleResult(result, format, rootPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        finally
        {
            if (implicitCommandFile is not null)
            {
                DeleteTemporaryFile(implicitCommandFile);
            }
        }
    }

    /// <summary>
    /// Runs named packages against the target directory (merged from cop check).
    /// Packages are auto-restored from configured GitHub feeds if not found locally.
    /// </summary>
    public static async Task<int> ExecutePackagesAsync(
        string[] packages, RunOptions? options = null)
    {
        options ??= new RunOptions();
        string? target = options.Target;
        string? format = options.OutputFormat;
        bool diag = options.IsDiagnosticEnabled;
        bool onlyIfModified = options.ShouldSkipUnmodifiedTargets;
        ReviewOptions? reviewOptions = options.ReviewOptions;
        string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();
        try { (reviewOptions ?? new ReviewOptions()).Validate(); }
        catch (ArgumentException ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 2; }

        // -om: skip analysis if no files modified in git working tree
        if (onlyIfModified && !await HasModifiedFilesAsync(rootPath))
        {
            return 0;
        }

        Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(ColorDiagLine(msg)) : null;

        // Discover feed paths from both rootPath and CWD (includes global cache)
        List<string> feedPaths = PackageResolver.GetFeedPaths(rootPath);
        string cwd = Directory.GetCurrentDirectory();
        if (!string.Equals(Path.GetFullPath(cwd), Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
        {
            foreach (string p in PackageResolver.GetFeedPaths(cwd))
            {
                if (!feedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    feedPaths.Add(p);
                }
            }
        }

        // Auto-restore any packages not found locally from configured GitHub feeds
        string cachePath = PackageResolver.GlobalCachePath;
        List<string> missing = FindMissingPackages(packages, feedPaths, cachePath);
        if (missing.Count > 0)
        {
            bool restored = await AutoRestorePackagesAsync(missing, cachePath);
            if (!restored)
            {
                return 2;
            }
        }

        // Ensure cache path is in feed list after restore
        if (Directory.Exists(cachePath) && !feedPaths.Contains(cachePath))
        {
            feedPaths.Add(cachePath);
        }

        if (feedPaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No package feeds found.");
            return 2;
        }

        // Convert rules filter
        List<string> rulesList = options.CommandNames?.ToList() ?? [];

        // Include user-global check files
        string[] userCheckFiles = await GetUserCheckFilesAsync(diagLog);

        try
        {
            EngineResult result = await Engine.RunProjectAsync(
                feedPaths, [.. packages], rootPath, rulesList, new EngineRunOptions
                {
                    DiagnosticLog = diagLog,
                    ScriptFiles = userCheckFiles.Length > 0 ? userCheckFiles : null,
                    ProviderNames = options.ProviderNames,
                    ReviewOptions = reviewOptions
                });
            return HandleResult(result, format, rootPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> ExecuteFromUriAsync(string uri, RunOptions options)
    {
        string[]? programArgs = options.Arguments?.ToArray();
        string? target = options.Target;
        string? format = options.OutputFormat;
        bool diag = options.IsDiagnosticEnabled;
        ReviewOptions? reviewOptions = options.ReviewOptions;
        if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Error: Only HTTPS URLs are supported for remote .cop files");
            return 1;
        }

        string? tempDir = null;
        try
        {
            // Download the .cop file
            using HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "cop-cli");

            using HttpResponseMessage response = await httpClient.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error: Failed to download '{uri}' (HTTP {(int)response.StatusCode})");
                return 1;
            }

            string content = await response.Content.ReadAsStringAsync();

            // Save to temp directory with .cop extension
            tempDir = Path.Combine(Path.GetTempPath(), $"cop-remote-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "remote.cop");
            File.WriteAllText(tempFile, content);

            // scriptsDir = temp dir, rootPath = CWD (or -t override)
            string scriptsDir = tempDir;
            string rootPath = !string.IsNullOrEmpty(target)
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
            string[]? commandFilter = options.CommandNames?.ToArray();

            // Pass CWD feed paths so locally-restored packages can resolve
            string[] additionalFeedPaths = FindFeedPathsFromCwd();

            Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(ColorDiagLine(msg)) : null;
            EngineResult result = await Engine.RunAsync(scriptsDir, rootPath, new EngineRunOptions
            {
                CommandName = commandName,
                ProgramArgs = programArgs,
                CommandFilter = commandFilter,
                DiagnosticLog = diagLog,
                AdditionalFeedPaths = additionalFeedPaths,
                ReviewOptions = reviewOptions,
            });

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
                DeleteTemporaryDirectory(tempDir);
            }
        }
    }

    private static bool IsUri(string value)
        => value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string[] FindFeedPathsFromCwd() =>
        PackageResolver.GetFeedPaths().ToArray();

    internal static int HandleResult(EngineResult result, string? format, string rootPath)
    {
        // Use structured diagnostics for rich error output when available
        if (result.Diagnostics is { Count: > 0 })
        {
            DiagnosticFormatter.WriteAllToStdErr(result.Diagnostics);
        }

        // Fall back to string errors for any not covered by structured diagnostics
        if (result.Diagnostics is null or { Count: 0 })
        {
            foreach (string error in result.ParseErrors)
            {
                Console.Error.WriteLine(error);
            }
        }

        if (result.Warnings is { Count: > 0 })
        {
            foreach (string warning in result.Warnings)
            {
                Console.Error.WriteLine(FormatProviderWarning(warning,
                    !Console.IsErrorRedirected && !ConsoleMarkdown.NoColor));
            }
        }

        if (result.HasParseErrors)
        {
            return 2;
        }

        if (result.HasFatalErrors)
        {
            // Only print string errors that aren't already represented by diagnostics
            if (result.Diagnostics is null or { Count: 0 })
            {
                foreach (string error in result.Errors)
                {
                    Console.Error.WriteLine(error);
                }
            }
            else
            {
                // Print non-diagnostic errors (e.g., runtime errors without source info)
                foreach (string error in result.Errors)
                {
                    if (!result.Diagnostics.Any(d => error.Contains(d.Message)))
                    {
                        Console.Error.WriteLine(error);
                    }
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
            foreach (PrintOutput output in result.Outputs)
            {
                Console.WriteLine(AnsiRenderer.Render(output.Content));
            }
        }

        // Write SAVE command outputs to files (paths are relative to codebase)
        if (result.FileOutputs is { Count: > 0 })
        {
            foreach (FileOutput output in result.FileOutputs)
            {
                string? filePath = Path.IsPathRooted(output.Path)
                    ? null  // reject absolute paths
                    : Path.GetFullPath(Path.Combine(rootPath, output.Path));

                if (filePath is null || !filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"SAVE error: path '{output.Path}' is outside the project root");
                    continue;
                }

                string? dir = Path.GetDirectoryName(filePath);
                if (dir is not null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(filePath, output.Content);
                Console.WriteLine($"SAVE: {output.Path}");
            }
        }

        if (!isJson && result.Statistics is { } statistics)
        {
            string elapsed = statistics.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string work = statistics.Evaluations.TryGetValue("check", out int checks)
                ? $"{checks} check{(checks == 1 ? "" : "s")} evaluated"
                : $"{statistics.CommandsExecuted} computation{(statistics.CommandsExecuted == 1 ? "" : "s")} evaluated";
            Console.Error.WriteLine(
                $"Summary: {statistics.InputFiles} input file{(statistics.InputFiles == 1 ? "" : "s")} processed, " +
                $"{work} in {elapsed}s.");
        }

        // Use explicit exit code from command if available (e.g., CHECK returns violation count)
        if (result.Warnings?.Any(w => w.StartsWith("Error:", StringComparison.Ordinal)) == true)
        {
            return 1;
        }

        if (result.ExitCode is not null)
        {
            return result.ExitCode.Value != 0 ? 1 : 0;
        }

        // Fallback: report output is success; CHECK-style violation output is failure.
        return result.Outputs.Any(IsViolationOutput) ? 1 : 0;
    }

    internal static string FormatProviderWarning(string warning, bool useColor) =>
        !useColor ? warning
        : (warning.StartsWith("Error:", StringComparison.Ordinal) ? ConsoleMarkdown.Red : ConsoleMarkdown.Yellow)
            + warning + ConsoleMarkdown.Reset;

    private static bool IsViolationOutput(PrintOutput output)
    {
        string message = output.Message;
        return message.Contains("): error:", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("): warning:", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("): info:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryCreateImplicitMainFile(string sourceFile)
    {
        string source;
        try
        {
            source = File.ReadAllText(sourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: Cannot read '{sourceFile}': {ex.Message}");
            return null;
        }

        List<string> expressions = ExtractTopLevelExpressions(source);
        if (expressions.Count == 0)
        {
            return null;
        }

        string generatedSource = string.Join(Environment.NewLine,
        [
            "command MAIN = {",
            .. expressions.Select(e => $"print({e})"),
            "}"
        ]);

        string scriptsDir = Path.GetDirectoryName(sourceFile) ?? Directory.GetCurrentDirectory();
        string generatedPath = Path.Combine(scriptsDir, $".cop-implicit-{Guid.NewGuid():N}.cop");

        try
        {
            Cop.Lang.Parser.CopParser.Parse(generatedSource, generatedPath);
            File.WriteAllText(generatedPath, generatedSource);
            return generatedPath;
        }
        catch (Exception ex) when (ex is ParseException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: Cannot create an implicit command for '{sourceFile}': {ex.Message}");
            DeleteTemporaryFile(generatedPath);
            return null;
        }
    }

    private static List<string> ExtractTopLevelExpressions(string source)
    {
        List<string> expressions = new List<string>();
        foreach (string rawLine in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = StripLineComment(rawLine).Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsTopLevelDeclarationLine(line))
            {
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal) ||
                line.StartsWith(".", StringComparison.Ordinal) ||
                line.StartsWith("&&", StringComparison.Ordinal) ||
                line.StartsWith("||", StringComparison.Ordinal) ||
                line.StartsWith(")", StringComparison.Ordinal) ||
                line.StartsWith("]", StringComparison.Ordinal) ||
                line.StartsWith("}", StringComparison.Ordinal))
            {
                return [];
            }

            expressions.Add(line);
        }

        return expressions;
    }

    private static string StripLineComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\'' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static bool IsTopLevelDeclarationLine(string line)
    {
        if (line.StartsWith("export ", StringComparison.Ordinal))
        {
            line = line["export ".Length..].TrimStart();
        }

        return StartsWithWord(line, "import") ||
               StartsWithWord(line, "feed") ||
               StartsWithWord(line, "type") ||
               StartsWithWord(line, "enum") ||
               StartsWithWord(line, "flags") ||
               StartsWithWord(line, "function") ||
               StartsWithWord(line, "predicate") ||
               StartsWithWord(line, "let") ||
               StartsWithWord(line, "command") ||
               StartsWithWord(line, "test") ||
               StartsWithWord(line, "RUN") ||
               StartsWithWord(line, "foreach") ||
               (StartsWithWord(line, "async") && line.Contains("foreach", StringComparison.Ordinal));
    }

    private static bool StartsWithWord(string value, string word)
    {
        if (!value.StartsWith(word, StringComparison.Ordinal))
        {
            return false;
        }

        return value.Length == word.Length || !char.IsLetterOrDigit(value[word.Length]) && value[word.Length] != '_';
    }

    private static void WriteOutputsAsJson(List<PrintOutput> outputs)
    {
        var items = outputs.Select(o => new { message = o.Message });
        string json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
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
        {
            return $"{cyan}{msg}{reset}";
        }

        if (msg.StartsWith("[debug]"))
        {
            return $"{magenta}{msg}{reset}";
        }
        // [diag] and everything else
        return $"{gray}{msg}{reset}";
    }

    /// <summary>
    /// Extracts command names defined in a specific .cop file (for single-file scoping).
    /// Commands are desugared to uppercase FunctionDecl by the parser.
    /// </summary>
    /// <summary>
    /// Returns the UPPERCASE command names declared in a .cop file, an empty array if it parses but
    /// declares none, or <c>null</c> if the file fails to parse (the parse error is reported here and
    /// the caller should exit with the fatal code).
    /// </summary>
    private static string[]? GetCommandNamesFromFile(string filePath)
    {
        try
        {
            string source = File.ReadAllText(filePath);
            ModuleNode module = Cop.Lang.Parser.CopParser.Parse(source, filePath);
            return module.Declarations
                .OfType<Cop.Lang.Ast.FunctionDecl>()
                .Where(f => f.Name.Length > 0 && f.Name == f.Name.ToUpperInvariant())
                .Select(c => c.Name)
                .ToArray();
        }
        catch (ParseException ex)
        {
            // Surface the parse error in the same "file(line): message" form as `cop verify`.
            Console.Error.WriteLine(ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error parsing '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Well-known directory for user-global check files.
    /// </summary>
    internal static string UserChecksDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop", "checks");

    /// <summary>
    /// Set by the --no-user-checks CLI flag to skip including user/global check files
    /// (replaces the former COP_NO_USER_CHECKS env var).
    /// </summary>
    internal static bool ShouldSkipUserChecks { get; set; }

    /// <summary>
    /// Discovers .cop files in ~/.cop/checks/ for user checks (hierarchical).
    /// - Top-level *.cop files are global (apply to all repos).
    /// - Subdirectory *.cop files are repo-specific (only when repo name matches).
    /// Returns empty array if the directory doesn't exist or if --no-user-checks was passed.
    /// </summary>
    private static async Task<string[]> GetUserCheckFilesAsync(Action<string>? diagLog)
    {
        if (ShouldSkipUserChecks)
        {
            return [];
        }

        string checksDir = UserChecksDirectory;
        if (!Directory.Exists(checksDir))
        {
            return [];
        }

        List<string> files = new List<string>();

        // 1. Top-level *.cop files are global (apply to all repos)
        files.AddRange(Directory.GetFiles(checksDir, "*.cop", SearchOption.TopDirectoryOnly));

        // 2. Repo-specific subdirectory (only if repo name matches)
        string? repoName = await GetCurrentRepoNameAsync();
        if (repoName != null)
        {
            string repoDir = Path.Combine(checksDir, repoName);
            if (Directory.Exists(repoDir))
            {
                files.AddRange(Directory.GetFiles(repoDir, "*.cop", SearchOption.AllDirectories));
                diagLog?.Invoke($"[diag] Including repo-specific user checks from {repoDir}");
            }
        }

        if (files.Count > 0)
        {
            diagLog?.Invoke($"[diag] Including {files.Count} user check file(s) from {checksDir}");
        }

        return files.ToArray();
    }

    /// <summary>
    /// Determines the current repository name for matching user check subdirectories.
    /// Resolution order: git remote origin URL → git root dir name → CWD name.
    /// </summary>
    private static async Task<string?> GetCurrentRepoNameAsync()
    {
        string workingDirectory = Directory.GetCurrentDirectory();
        ToolchainResult? remote = await RunGitAsync(workingDirectory,
            ["remote", "get-url", "origin"]);
        if (remote is { Status: ToolchainStatus.Success } &&
            !string.IsNullOrWhiteSpace(remote.StandardOutput))
        {
            string name = remote.StandardOutput.Trim().TrimEnd('/');
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }
            int separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf(':'));
            if (separator >= 0 && separator < name.Length - 1)
            {
                return name[(separator + 1)..];
            }
        }

        ToolchainResult? root = await RunGitAsync(workingDirectory,
            ["rev-parse", "--show-toplevel"]);
        return root is { Status: ToolchainStatus.Success } &&
            !string.IsNullOrWhiteSpace(root.StandardOutput)
                ? GetDirectoryName(root.StandardOutput.Trim())
                : GetDirectoryName(workingDirectory);
    }

    private static async Task<ToolchainResult?> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments)
    {
        string? executable = ToolchainProcess.FindExecutable("git");
        if (executable is null)
        {
            return null;
        }
        ToolchainResult result = await ToolchainProcess.RunAsync(
            new ToolchainCommand(executable, arguments, workingDirectory),
            new ToolchainRunOptions { Timeout = TimeSpan.FromSeconds(5) });
        if (result.Status != ToolchainStatus.Success && CopDiagnostics.Level > 0)
        {
            Console.Error.WriteLine(
                $"[diag] Git discovery failed ({result.Status}, exit {result.ExitCode}).");
        }
        return result;
    }

    private static string? GetDirectoryName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Checks if the working tree has modified files using git status.
    /// Returns true if files are modified or if git is unavailable (safe fallback).
    /// </summary>
    private static async Task<bool> HasModifiedFilesAsync(string workingDir)
    {
        ToolchainResult? result = await RunGitAsync(workingDir, ["status", "--porcelain"]);
        if (result is not { Status: ToolchainStatus.Success })
        {
            Console.Error.WriteLine(
                "Warning: Cannot read Git status; running checks without skipping unmodified targets.");
            return true;
        }
        return !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    /// <summary>
    /// Parses .cop files in the scripts directory, discovers imports, and auto-restores
    /// any missing packages into the current host version's cache.
    /// </summary>
    private static async Task AutoRestoreImportsAsync(
        string scriptsDir, Action<string>? diagLog)
    {
        string cachePath = PackageResolver.GlobalCachePath;

        List<string> feedPaths = PackageResolver.GetFeedPaths();
        foreach (string p in PackageResolver.GetFeedPaths(scriptsDir))
        {
            if (!feedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                feedPaths.Add(p);
            }
        }

        // Parse all .cop files to collect imports
        HashSet<string> imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        foreach (string path in scriptFilePaths)
        {
            try
            {
                string source = File.ReadAllText(path);
                ModuleNode module = Cop.Lang.Parser.CopParser.Parse(source, path);
                foreach (ImportDecl imp in module.Declarations.OfType<Cop.Lang.Ast.ImportDecl>())
                {
                    imports.Add(imp.ModuleName);
                }

                AddDeclaredFeedPaths(feedPaths, module, path);
            }
            catch (Exception ex) when (ex is ParseException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Warning: Cannot inspect imports in '{path}': {ex.Message}");
            }
        }

        if (imports.Count == 0)
        {
            return;
        }

        HashSet<string> incompatibleProviders = new HashSet<string>(StringComparer.Ordinal);
        string? FindCompatiblePackage(string name) =>
            PackageResolver.FindPackageDir(name, feedPaths, error =>
            {
                if (incompatibleProviders.Add(error))
                {
                    diagLog?.Invoke($"[diag] Skipping local provider: {error}");
                }
            });

        void ReportCompatibilityResolved()
        {
            if (incompatibleProviders.Count > 0)
            {
                Console.Error.WriteLine($"Info: Packages are ready for Cop {ProviderCompatibility.HostVersion}.");
            }
        }

        Queue<string> pendingImports = new Queue<string>(imports);
        while (pendingImports.TryDequeue(out string? name))
        {
            if (FindCompatiblePackage(name) is not { } packageDir)
            {
                continue;
            }

            foreach (string import in ReadPackageImports(packageDir))
            {
                if (imports.Add(import))
                {
                    pendingImports.Enqueue(import);
                }
            }
        }

        // Find imports that can't be resolved from any known path
        List<string> missing = new List<string>();
        foreach (string imp in imports)
        {
            if (FindCompatiblePackage(imp) is null)
            {
                missing.Add(imp);
            }
        }

        if (missing.Count == 0)
        {
            ReportCompatibilityResolved();
            return;
        }

        // Try to restore from GitHub feeds
        FeedManager feedManager = new FeedManager();
        List<string> feeds = feedManager.GetFeeds();
        List<string> githubFeeds = feeds
            .Where(f => f.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (githubFeeds.Count == 0)
        {
            diagLog?.Invoke($"[diag] Missing imports ({string.Join(", ", missing)}) but no GitHub feeds configured");
            return;
        }

        string? githubToken = await GitHubAuthentication.ResolveTokenAsync();
        using HttpClient httpClient = new HttpClient();
        GitHubPackageSource source2 = new GitHubPackageSource(httpClient, githubToken);

        Directory.CreateDirectory(cachePath);
        if (!feedPaths.Contains(cachePath, StringComparer.OrdinalIgnoreCase))
        {
            feedPaths.Add(cachePath);
        }

        // BFS: download missing packages and their transitive imports
        Queue<string> queue = new Queue<string>(missing);
        HashSet<string> downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int restoredCount = 0;

        if (missing.Count > 0)
        {
            int feedCount = githubFeeds.Distinct().Count();
            if (incompatibleProviders.Count > 0)
            {
                Console.Error.WriteLine("Info: Local packages target a different Cop version. " +
                    "Preparing matching packages automatically; local files will not be changed.");
            }

            Console.Error.WriteLine($"Auto-restoring {missing.Count} package(s) from {feedCount} feed(s)...");
        }

        while (queue.Count > 0)
        {
            string pkgName = queue.Dequeue();
            if (downloaded.Contains(pkgName))
            {
                continue;
            }

            if (FindCompatiblePackage(pkgName) is { } existingPackage)
            {
                downloaded.Add(pkgName);
                foreach (string import in ReadPackageImports(existingPackage))
                {
                    if (!downloaded.Contains(import))
                    {
                        imports.Add(import);
                        queue.Enqueue(import);
                    }
                }

                continue;
            }

            bool restored = false;
            foreach (string feed in githubFeeds)
            {
                try
                {
                    PackageReference pkgRef = PackageReference.Parse($"{feed}/{pkgName}");
                    Console.Error.Write($"  Restoring '{pkgName}' from {feed}...");
                    Dictionary<string, byte[]> files =
                        await source2.DownloadPackageFilesAsync(pkgRef);

                    if (files.Count == 0) { Console.Error.WriteLine(" no files"); continue; }

                    // Write to temp directory first, then atomically move into place.
                    // This prevents concurrent cop processes from reading partially-written packages.
                    string pkgDir = Path.Combine(cachePath, pkgName);
                    string tempDir = Path.Combine(cachePath, $".{pkgName}.tmp.{Environment.ProcessId}");
                    try
                    {
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, recursive: true);
                        }

                        foreach ((string? relativePath, byte[]? content) in files)
                        {
                            string destPath = ValidatePackagePath(relativePath, tempDir);
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            File.WriteAllBytes(destPath, content);
                        }

                        // Place the package. We only reach here when no VALID package (one
                        // containing cop.json) was found above, so any existing pkgDir is
                        // stale/incomplete and must be replaced — unless another process
                        // concurrently placed a complete package in the meantime.
                        PackageInstaller.PlaceRestoredPackage(tempDir, pkgDir);
                    }
                    finally
                    {
                        DeleteTemporaryDirectory(tempDir);
                    }

                    Console.Error.WriteLine(" ok");
                    downloaded.Add(pkgName);
                    restored = true;
                    restoredCount++;

                    // Parse src/ .cop files to discover transitive imports (skip samples/)
                    foreach ((string? relPath, byte[] _) in files.Where(f =>
                        f.Key.EndsWith(".cop", StringComparison.OrdinalIgnoreCase) &&
                        f.Key.StartsWith("src/", StringComparison.OrdinalIgnoreCase)))
                    {
                        string filePath = Path.Combine(pkgDir, relPath);
                        try
                        {
                            string src = File.ReadAllText(filePath);
                            ScriptFile sf = Cop.Lang.Parser.CopParser.ParseFile(src, filePath);
                            foreach (string imp in sf.Imports)
                            {
                                if (!downloaded.Contains(imp))
                                {
                                    imports.Add(imp);
                                    queue.Enqueue(imp);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is ParseException or IOException or UnauthorizedAccessException)
                        {
                            Console.Error.WriteLine($"Warning: Cannot inspect imports in '{filePath}': {ex.Message}");
                        }
                    }
                    break;
                }
                catch (PackageNotFoundException)
                {
                    Console.Error.WriteLine(" not found");
                    continue;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or
                    UnauthorizedAccessException or FormatException or InvalidOperationException or
                    System.Text.Json.JsonException)
                {
                    Console.Error.WriteLine($" failed: {ex.Message}");
                }
            }

            if (!restored)
            {
                Console.Error.WriteLine($"Warning: Package '{pkgName}' not found in any configured feed.");
            }
        }

        if (restoredCount > 0)
        {
            Console.Error.WriteLine($"{restoredCount} package(s) restored successfully");
            if (imports.All(name => FindCompatiblePackage(name) is not null))
            {
                ReportCompatibilityResolved();
                Console.Error.WriteLine("Package setup complete. Continuing source analysis.");
            }
        }
    }

    private static void AddDeclaredFeedPaths(List<string> feedPaths, Cop.Lang.Ast.ModuleNode module, string filePath)
    {
        foreach (string feed in Cop.Lang.Interpreter.ModuleLoader.ExtractFeedPaths(module,
            Path.GetDirectoryName(Path.GetFullPath(filePath))!))
        {
            if (feedPaths.Contains(feed, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            int cacheIndex = feedPaths.FindIndex(p =>
                string.Equals(p, PackageResolver.GlobalCachePath, StringComparison.OrdinalIgnoreCase));
            feedPaths.Insert(cacheIndex < 0 ? feedPaths.Count : cacheIndex, feed);
        }
    }

    /// <summary>
    /// Places the instructions of every package imported (transitively) by the .cop files in
    /// <paramref name="scriptsDir"/> into <paramref name="repoRoot"/>/.github/instructions/, so
    /// coding agents pick them up. Resolves each import to a package directory across the known
    /// feed paths, then delegates to <see cref="InstructionPlacement.PlaceFromPackageDir"/>
    /// (which is idempotent and a no-op for packages without an instructions/ folder).
    /// </summary>
    private static void PlaceInstructionsForImports(string scriptsDir, string repoRoot, Action<string>? diagLog)
    {
        try
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> queue = new Queue<string>();
            List<string> feedPaths = PackageResolver.GetFeedPaths();
            foreach (string p in PackageResolver.GetFeedPaths(scriptsDir))
            {
                if (!feedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    feedPaths.Add(p);
                }
            }

            void EnqueueImportsFrom(string dir)
            {
                if (!Directory.Exists(dir))
                {
                    return;
                }

                foreach (string path in Directory.GetFiles(dir, "*.cop", SearchOption.AllDirectories))
                {
                    try
                    {
                        ModuleNode module = Cop.Lang.Parser.CopParser.Parse(File.ReadAllText(path), path);
                        foreach (ImportDecl imp in module.Declarations.OfType<Cop.Lang.Ast.ImportDecl>())
                        {
                            if (seen.Add(imp.ModuleName))
                            {
                                queue.Enqueue(imp.ModuleName);
                            }
                        }

                        AddDeclaredFeedPaths(feedPaths, module, path);
                    }
                    catch (Exception ex) when (ex is ParseException or IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"Warning: Cannot inspect imports in '{path}': {ex.Message}");
                    }
                }
            }

            EnqueueImportsFrom(scriptsDir);
            if (queue.Count == 0)
            {
                return;
            }

            while (queue.Count > 0)
            {
                string name = queue.Dequeue();

                string? pkgDir = null;
                foreach (string feed in feedPaths)
                {
                    pkgDir = ImportResolver.FindPackageDir(feed, name);
                    if (pkgDir != null)
                    {
                        break;
                    }
                }
                if (pkgDir == null)
                {
                    continue;
                }

                try
                {
                    string? target = InstructionPlacement.PlaceFromPackageDir(repoRoot, pkgDir, out bool wrote);
                    if (target != null && wrote)
                    {
                        diagLog?.Invoke($"[diag] Placed instructions for '{name}' -> {target}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Warning: Cannot place instructions for '{name}': {ex.Message}");
                }

                // Follow the package's own imports (types/ takes priority over src/).
                string typesDir = Path.Combine(pkgDir, "types");
                EnqueueImportsFrom(Directory.Exists(typesDir) ? typesDir : Path.Combine(pkgDir, "src"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Instruction placement failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns package names that cannot be found in any local feed path or cache.
    /// </summary>
    private static List<string> FindMissingPackages(string[] packages, List<string> feedPaths, string cachePath)
    {
        List<string> allPaths = new List<string>(feedPaths);
        if (Directory.Exists(cachePath))
        {
            allPaths.Add(cachePath);
        }

        List<string> missing = new List<string>();
        foreach (string pkg in packages)
        {
            bool found = false;
            foreach (string feed in allPaths)
            {
                if (ImportResolver.FindPackageDir(Path.GetFullPath(feed), pkg) is not null)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                missing.Add(pkg);
            }
        }
        return missing;
    }

    /// <summary>
    /// Downloads missing packages from configured GitHub feeds into the cache directory.
    /// Recursively resolves imports from downloaded .cop files.
    /// </summary>
    /// <summary>
    /// Resolves the analysis target (root) directory. When <paramref name="target"/>
    /// (the <c>-t</c> flag) is empty, the target is the current working directory — NOT
    /// the folder containing the named .cop file. This is what makes
    /// <c>cop cop-checks/main.cop</c> (run from the repo root) analyze the repo root.
    /// </summary>
    public static string ResolveTargetRoot(string? target, string currentDirectory) =>
        string.IsNullOrEmpty(target)
            ? Path.GetFullPath(currentDirectory)
            : Path.GetFullPath(target);

    /// <summary>Restores missing packages into the cache and reports whether all restores succeeded.</summary>
    public static async Task<bool> AutoRestorePackagesAsync(List<string> packageNames, string cachePath)
    {
        FeedManager feedManager = new FeedManager();
        List<string> feeds = feedManager.GetFeeds();

        List<string> githubFeeds = feeds
            .Where(f => f.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (githubFeeds.Count == 0)
        {
            Console.Error.WriteLine("Error: No GitHub feeds configured. Run 'cop feed add github.com/owner/repo'.");
            return false;
        }

        string? githubToken = await GitHubAuthentication.ResolveTokenAsync();
        using HttpClient httpClient = new HttpClient();
        GitHubPackageSource source = new GitHubPackageSource(httpClient, githubToken);

        Directory.CreateDirectory(cachePath);

        // BFS: download requested packages, then their imports
        Queue<string> queue = new Queue<string>(packageNames);
        HashSet<string> downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int restoredCount = 0;

        if (packageNames.Count > 0)
        {
            int feedCount = githubFeeds.Distinct().Count();
            Console.Error.WriteLine($"Auto-restoring {packageNames.Count} package(s) from {feedCount} feed(s)...");
        }

        while (queue.Count > 0)
        {
            string pkgName = queue.Dequeue();
            if (downloaded.Contains(pkgName))
            {
                continue;
            }

            if (ImportResolver.FindPackageDir(cachePath, pkgName) is not null)
            {
                downloaded.Add(pkgName);
                continue;
            }

            bool restored = false;
            foreach (string feed in githubFeeds)
            {
                try
                {
                    PackageReference pkgRef = PackageReference.Parse($"{feed}/{pkgName}");
                    Console.Error.Write($"  Restoring '{pkgName}' from {feed}...");
                    Dictionary<string, byte[]> files = await source.DownloadPackageFilesAsync(pkgRef);

                    if (files.Count == 0) { Console.Error.WriteLine(" no files"); continue; }

                    // Write to temp directory first, then atomically move into place.
                    // This prevents concurrent cop processes from reading partially-written packages.
                    string pkgDir = Path.Combine(cachePath, pkgName);
                    string tempDir = Path.Combine(cachePath, $".{pkgName}.tmp.{Environment.ProcessId}");
                    try
                    {
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, recursive: true);
                        }

                        foreach ((string? relativePath, byte[]? content) in files)
                        {
                            string destPath = ValidatePackagePath(relativePath, tempDir);
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            await File.WriteAllBytesAsync(destPath, content);
                        }

                        // Place the package. We only reach here when no VALID package (one
                        // containing cop.json) was found above, so any existing pkgDir is
                        // stale/incomplete and must be replaced — unless another process
                        // concurrently placed a complete package in the meantime.
                        PackageInstaller.PlaceRestoredPackage(tempDir, pkgDir);
                    }
                    finally
                    {
                        DeleteTemporaryDirectory(tempDir);
                    }

                    Console.Error.WriteLine(" ok");
                    downloaded.Add(pkgName);
                    restored = true;
                    restoredCount++;

                    // Parse src/ .cop files to discover transitive imports (skip samples/)
                    foreach ((string? relPath, byte[] _) in files.Where(f =>
                        f.Key.EndsWith(".cop", StringComparison.OrdinalIgnoreCase) &&
                        f.Key.StartsWith("src/", StringComparison.OrdinalIgnoreCase)))
                    {
                        string filePath = Path.Combine(pkgDir, relPath);
                        try
                        {
                            string src = File.ReadAllText(filePath);
                            ScriptFile sf = Cop.Lang.Parser.CopParser.ParseFile(src, filePath);
                            foreach (string imp in sf.Imports)
                            {
                                if (!downloaded.Contains(imp))
                                {
                                    queue.Enqueue(imp);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is ParseException or IOException or UnauthorizedAccessException)
                        {
                            Console.Error.WriteLine($"Warning: Cannot inspect imports in '{filePath}': {ex.Message}");
                        }
                    }
                    break;
                }
                catch (PackageNotFoundException)
                {
                    Console.Error.WriteLine(" not found");
                    continue;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or
                    UnauthorizedAccessException or FormatException or InvalidOperationException or
                    System.Text.Json.JsonException)
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

        if (restoredCount > 0)
        {
            Console.Error.WriteLine($"{restoredCount} package(s) restored successfully");
        }

        return true;
    }

    private static IEnumerable<string> ReadPackageImports(string packageDir)
    {
        string srcDir = Path.Combine(packageDir, "src");
        if (!Directory.Exists(srcDir))
        {
            yield break;
        }

        foreach (string path in Directory.GetFiles(srcDir, "*.cop"))
        {
            ModuleNode module = Cop.Lang.Parser.CopParser.ParseWithRecovery(File.ReadAllText(path), path, out _);
            foreach (ImportDecl import in module.Declarations.OfType<Cop.Lang.Ast.ImportDecl>())
            {
                yield return import.ModuleName;
            }
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Cannot remove temporary file '{path}': {ex.Message}");
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Cannot remove temporary directory '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that a relative path from a package doesn't escape the intended directory.
    /// Prevents path traversal attacks via sequences like ../../ in package file paths.
    /// </summary>
    private static string ValidatePackagePath(string relativePath, string baseDir)
    {
        // Normalize the path by combining with a dummy base and checking it stays within bounds
        string testBase = Path.GetFullPath("dummy");
        string testPath = Path.GetFullPath(Path.Combine("dummy", relativePath));

        if (!testPath.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !testPath.Equals(testBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Invalid package path (potential path traversal): {relativePath}");
        }

        // Return the validated combined path
        return Path.Combine(baseDir, relativePath);
    }
}
