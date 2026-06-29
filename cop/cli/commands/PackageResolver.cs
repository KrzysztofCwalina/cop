using Cop.Lang;

namespace Cop.Cli.Commands;

/// <summary>
/// Shared utility for locating packages across feed paths.
/// Consolidates the walk-up-from-cwd + global-cache + auto-restore pattern
/// used by help, list, run, and other commands.
/// </summary>
internal static class PackageResolver
{
    /// <summary>
    /// Returns the global package cache path (~/.cop/packages/).
    /// </summary>
    public static string GlobalCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cop", "packages");

    /// <summary>
    /// Discovers all feed paths by walking up from a starting directory
    /// and including the global cache. Resolution order (first match wins):
    /// 1. Local packages/ directories, walking up from startDir — so a developer's in-repo
    ///    package always shadows a possibly-stale auto-restored copy in the global cache.
    /// 2. ~/.cop/packages/ (global cache for auto-restored packages) — the fallback.
    /// </summary>
    public static List<string> GetFeedPaths(string? startDir = null)
    {
        var paths = new List<string>();

        // 1. Local packages/ dirs take precedence.
        var dir = startDir ?? Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
                paths.Add(packagesDir);
            dir = Path.GetDirectoryName(dir);
        }

        // 2. Global cache is the fallback.
        var cachePath = GlobalCachePath;
        if (Directory.Exists(cachePath))
            paths.Add(cachePath);

        return paths;
    }

    /// <summary>
    /// Resolves a package name to its directory path by searching all feed paths.
    /// Returns null if the package is not found in any feed.
    /// </summary>
    public static string? FindPackageDir(string packageName, List<string>? feedPaths = null)
    {
        feedPaths ??= GetFeedPaths();

        foreach (var feed in feedPaths)
        {
            var dir = ImportResolver.FindPackageDir(Path.GetFullPath(feed), packageName);
            if (dir != null)
                return dir;
        }

        return null;
    }

    /// <summary>
    /// Resolves a package name to its directory, auto-restoring from GitHub feeds if not found locally.
    /// </summary>
    public static string? ResolvePackageDir(string packageName, List<string>? feedPaths = null)
    {
        feedPaths ??= GetFeedPaths();

        var dir = FindPackageDir(packageName, feedPaths);
        if (dir != null) return dir;

        // Auto-restore from configured feeds
        var cachePath = GlobalCachePath;
        var restored = RunCommand.AutoRestorePackagesAsync([packageName], cachePath).GetAwaiter().GetResult();
        if (restored)
            return ImportResolver.FindPackageDir(cachePath, packageName);

        return null;
    }
}
