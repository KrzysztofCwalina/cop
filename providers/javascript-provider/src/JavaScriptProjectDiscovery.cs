using System.Text.Json;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Discovers JavaScript/TypeScript projects by scanning for package.json files,
/// extracting project name and dependencies.
/// </summary>
public static class JavaScriptProjectDiscovery
{
    /// <summary>
    /// Discovers JS/TS projects under rootPath by finding package.json files.
    /// </summary>
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs)
    {
        var manifestPaths = new List<string>();
        CollectManifests(rootPath, excludedDirs, manifestPaths);

        var result = new List<ProjectInfo>();
        foreach (var manifestPath in manifestPaths)
        {
            var info = ParsePackageJson(manifestPath, rootPath);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    private static ProjectInfo? ParsePackageJson(string filePath, string rootPath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Extract name
            if (!root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                return null;

            var name = nameElement.GetString()!;
            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            var dependencies = new List<string>();

            // Extract dependencies
            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in deps.EnumerateObject())
                    dependencies.Add(prop.Name);
            }

            // Extract devDependencies
            if (root.TryGetProperty("devDependencies", out var devDeps) && devDeps.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in devDeps.EnumerateObject())
                    dependencies.Add(prop.Name);
            }

            return new ProjectInfo(name, relativePath, "javascript", dependencies);
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
            var packageJson = Path.Combine(dir, "package.json");
            if (File.Exists(packageJson))
            {
                result.Add(packageJson);
                // Don't recurse into node_modules or sub-packages of this project
                // But do recurse into workspaces (sibling dirs)
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                if (dirName == "node_modules") continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
