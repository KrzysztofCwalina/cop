using System.Text.RegularExpressions;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Discovers Go projects by scanning for go.mod files,
/// extracting module name and dependencies.
/// </summary>
public static class GoProjectDiscovery
{
    /// <summary>
    /// Discovers Go projects under rootPath by finding go.mod files.
    /// </summary>
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs)
    {
        var manifestPaths = new List<string>();
        CollectManifests(rootPath, excludedDirs, manifestPaths);

        var result = new List<ProjectInfo>();
        foreach (var manifestPath in manifestPaths)
        {
            var info = ParseGoMod(manifestPath, rootPath);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    private static ProjectInfo? ParseGoMod(string filePath, string rootPath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            string? moduleName = null;
            var dependencies = new List<string>();
            bool inRequire = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // module declaration
                var moduleMatch = Regex.Match(trimmed, @"^module\s+(\S+)");
                if (moduleMatch.Success)
                {
                    moduleName = moduleMatch.Groups[1].Value;
                    continue;
                }

                // require block
                if (trimmed == "require (")
                {
                    inRequire = true;
                    continue;
                }
                if (trimmed == ")" && inRequire)
                {
                    inRequire = false;
                    continue;
                }

                // Single-line require
                var singleReq = Regex.Match(trimmed, @"^require\s+(\S+)\s+");
                if (singleReq.Success)
                {
                    dependencies.Add(singleReq.Groups[1].Value);
                    continue;
                }

                if (inRequire)
                {
                    var depMatch = Regex.Match(trimmed, @"^(\S+)\s+");
                    if (depMatch.Success && !trimmed.StartsWith("//"))
                        dependencies.Add(depMatch.Groups[1].Value);
                }
            }

            if (moduleName is null)
                return null;

            // Use last path segment as project name
            var name = moduleName.Contains('/') ? moduleName[(moduleName.LastIndexOf('/') + 1)..] : moduleName;

            return new ProjectInfo(name, relativePath, "go", dependencies, dependencies, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void CollectManifests(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            var goMod = Path.Combine(dir, "go.mod");
            if (File.Exists(goMod))
            {
                result.Add(goMod);
                return; // Don't recurse into sub-modules
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                if (dirName == "vendor") continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
