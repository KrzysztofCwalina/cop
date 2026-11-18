// Implements `cop checks`: generates and opens the check catalogue.

using System.Diagnostics;
using System.Text;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Cli.Commands;

/// <summary>Creates a static, standalone HTML inventory of Cop checks.</summary>
internal static class CheckCatalogueCommand
{
    /// <summary>Runs <c>cop checks</c> with the arguments that follow the command name.</summary>
    public static async Task<int> ExecuteFromArgsAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "rename")
        {
            return CheckRenameCommand.Execute(args[1..]);
        }

        if (!TryParseArguments(args, out string? inputPath, out string? outputPath,
            out bool openBrowser, out string? error))
        {
            Console.Error.WriteLine($"cop checks: {error}");
            Console.Error.WriteLine("usage: cop checks [<file.cop|directory>] [--output <file>] [--open]");
            return 2;
        }

        string requestedPath = inputPath ?? Path.Combine(Directory.GetCurrentDirectory(), "cop-checks");
        string rootPath;
        string[] localFiles;
        try
        {
            string fullPath = Path.GetFullPath(requestedPath);
            if (Directory.Exists(fullPath))
            {
                rootPath = fullPath;
                localFiles = Directory.GetFiles(rootPath, "*.cop", SearchOption.AllDirectories);
            }
            else if (File.Exists(fullPath) &&
                string.Equals(Path.GetExtension(fullPath), ".cop", StringComparison.OrdinalIgnoreCase))
            {
                rootPath = Path.GetDirectoryName(fullPath)!;
                localFiles = [fullPath];
            }
            else
            {
                Console.Error.WriteLine($"cop checks: input path not found: {fullPath}");
                return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"cop checks: cannot discover source files: {ex.Message}");
            return 1;
        }

        if (localFiles.Length == 0)
        {
            Console.Error.WriteLine($"cop checks: no .cop files found under {rootPath}");
            return 1;
        }
        Array.Sort(localFiles, StringComparer.Ordinal);

        List<CheckCatalogueSource> localSources = [];
        foreach (string filePath in localFiles)
        {
            try
            {
                string source = File.ReadAllText(filePath);
                ModuleNode module = CopParser.Parse(source, filePath);
                localSources.Add(new CheckCatalogueSource(module, filePath, source, null));
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                Console.Error.WriteLine($"cop checks: cannot read '{filePath}': {ex.Message}");
                return 1;
            }
        }

        List<string> feedPaths = GetFeedPaths(rootPath, localSources);
        Evaluator evaluator = new Evaluator();
        ModuleLoader loader = new ModuleLoader(feedPaths);
        loader.ResolveImports(localSources.Select(source => source.Module), evaluator);

        Dictionary<ModuleNode, string> packageByModule =
            new(ReferenceEqualityComparer.Instance);
        foreach ((string packageName, List<ModuleNode> modules) in loader.PackageModules)
        {
            foreach (ModuleNode module in modules)
            {
                packageByModule[module] = packageName;
            }
        }

        List<CheckCatalogueSource> allSources = [.. localSources];
        foreach ((ModuleNode module, string filePath, string source) in loader.LoadedFiles)
        {
            packageByModule.TryGetValue(module, out string? packageName);
            allSources.Add(new CheckCatalogueSource(module, filePath, source, packageName));
        }

        CheckCatalogueAnalysis analysis = CheckCatalogueAnalyzer.Analyze(
            rootPath, allSources, loader.Errors);

        string fullOutputPath = outputPath is null
            ? Path.Combine(Path.GetTempPath(), $"cop-checks-{Guid.NewGuid():N}.html")
            : Path.GetFullPath(outputPath);
        string title = $"Cop check catalogue — {Path.GetFileName(rootPath)}";
        string html = CheckCatalogueHtml.Render(title, analysis);
        try
        {
            string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
            await File.WriteAllTextAsync(fullOutputPath, html, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"cop checks: cannot write '{fullOutputPath}': {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Check catalogue: {fullOutputPath}");
        foreach (string warning in analysis.Warnings)
        {
            Console.Error.WriteLine($"Warning: {warning}");
        }

        if (openBrowser || outputPath is null)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = fullOutputPath,
                    UseShellExecute = true
                });
                if (process is null)
                {
                    throw new InvalidOperationException("The operating system did not start a browser.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                PlatformNotSupportedException)
            {
                Console.Error.WriteLine(
                    $"cop checks: catalogue was saved, but the browser could not be opened: {ex.Message}");
                return 1;
            }
        }

        return analysis.Warnings.Count == 0 ? 0 : 1;
    }

    private static bool TryParseArguments(
        string[] args,
        out string? inputPath,
        out string? outputPath,
        out bool openBrowser,
        out string? error)
    {
        inputPath = null;
        outputPath = null;
        openBrowser = false;
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument is "--open")
            {
                openBrowser = true;
            }
            else if (argument is "--output" or "-o")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    error = $"option '{argument}' requires a file path.";
                    return false;
                }
                outputPath = args[i];
            }
            else if (argument.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
            {
                string value = argument["--output=".Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = "option '--output' requires a file path.";
                    return false;
                }
                outputPath = value;
            }
            else if (argument.StartsWith('-'))
            {
                error = $"unknown option '{argument}'.";
                return false;
            }
            else if (inputPath is null)
            {
                inputPath = argument;
            }
            else
            {
                error = "only one source file or directory may be specified.";
                return false;
            }
        }

        return true;
    }

    internal static List<string> GetFeedPaths(
        string rootPath,
        IReadOnlyList<CheckCatalogueSource> localSources)
    {
        List<string> feedPaths = PackageResolver.GetFeedPaths(rootPath);
        string globalCache = PackageResolver.GlobalCachePath;
        foreach (CheckCatalogueSource source in localSources)
        {
            string sourceDirectory = Path.GetDirectoryName(source.FilePath) ?? rootPath;
            foreach (string feedPath in ModuleLoader.ExtractFeedPaths(source.Module, sourceDirectory))
            {
                if (feedPaths.Contains(feedPath, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                int cacheIndex = feedPaths.FindIndex(path =>
                    string.Equals(path, globalCache, StringComparison.OrdinalIgnoreCase));
                if (cacheIndex >= 0)
                {
                    feedPaths.Insert(cacheIndex, feedPath);
                }
                else
                {
                    feedPaths.Add(feedPath);
                }
            }
        }

        return feedPaths;
    }
}
