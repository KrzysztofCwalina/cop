using System.CommandLine;
using Cop.Core;
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
            Description = "One or more package names to run (e.g., csharp-style fdg)"
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
        command.SetAction(parseResult =>
        {
            var task = ExecuteAsync(
                parseResult.GetValue(packagesArg)!,
                parseResult.GetValue(targetOption),
                parseResult.GetValue(rulesOption),
                parseResult.GetValue(formatOption),
                parseResult.GetValue(diagOption));
            return task.GetAwaiter().GetResult();
        });
        return command;
    }

    public static async Task<int> ExecuteAsync(string[] packages, string? target = null, string? rules = null, string? format = null, bool diag = false)
    {
        string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();

        // Discover local feed paths (walking up from target to find packages/ dirs)
        var feedPaths = FindFeedPaths(rootPath);

        // ~/.cop/packages/ is always a feed path (auto-restored packages live here)
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "packages");

        // Auto-restore any packages not found locally from configured GitHub feeds
        var missing = FindMissingPackages(packages, feedPaths, cachePath);
        if (missing.Count > 0)
        {
            var restored = await AutoRestoreAsync(missing, cachePath);
            if (!restored)
                return 2;
        }

        // Ensure cache path is in feed list
        if (Directory.Exists(cachePath) && !feedPaths.Contains(cachePath))
            feedPaths.Add(cachePath);

        if (feedPaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No package feeds found.");
            return 2;
        }

        // Parse rules filter
        var rulesList = new List<string>();
        if (!string.IsNullOrEmpty(rules))
            rulesList.AddRange(rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var result = Engine.RunProject(feedPaths, [.. packages], rootPath, rulesList);

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
    private static async Task<bool> AutoRestoreAsync(List<string> packageNames, string cachePath)
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

        string? githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        using var httpClient = new HttpClient();
        var source = new GitHubPackageSource(httpClient, githubToken);

        Directory.CreateDirectory(cachePath);

        // BFS: download requested packages, then their imports, then their imports' imports, etc.
        var queue = new Queue<string>(packageNames);
        var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var pkgName = queue.Dequeue();
            if (downloaded.Contains(pkgName)) continue;

            // Already cached locally?
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
                    Console.Error.Write($"  Downloading {pkgName}...");
                    var files = await source.DownloadPackageFilesAsync(pkgRef);

                    if (files.Count == 0) { Console.Error.WriteLine(" no files"); continue; }

                    var pkgDir = Path.Combine(cachePath, pkgName);
                    foreach (var (relativePath, content) in files)
                    {
                        var destPath = Path.Combine(pkgDir, relativePath);
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
            {
                Console.Error.WriteLine($"Error: Package '{pkgName}' not found in any configured feed.");
                return false;
            }
        }

        return true;
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
