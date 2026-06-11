using System.Text.RegularExpressions;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Discovers Rust projects by scanning for Cargo.toml files,
/// extracting project name and dependencies.
/// </summary>
public static class RustProjectDiscovery
{
    /// <summary>
    /// Discovers Rust projects under rootPath by finding Cargo.toml files.
    /// </summary>
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs)
    {
        var manifestPaths = new List<string>();
        CollectManifests(rootPath, excludedDirs, manifestPaths);

        var result = new List<ProjectInfo>();
        foreach (var manifestPath in manifestPaths)
        {
            var info = ParseCargoToml(manifestPath, rootPath);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    private static ProjectInfo? ParseCargoToml(string filePath, string rootPath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            string? name = null;
            var dependencies = new List<string>();
            bool inPackage = false;
            bool inDependencies = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Track sections
                if (trimmed.StartsWith("["))
                {
                    inPackage = trimmed == "[package]";
                    inDependencies = trimmed == "[dependencies]" || trimmed == "[dev-dependencies]"
                        || trimmed == "[build-dependencies]";
                    continue;
                }

                if (inPackage)
                {
                    var nameMatch = Regex.Match(trimmed, @"^name\s*=\s*""([^""]+)""");
                    if (nameMatch.Success)
                        name = nameMatch.Groups[1].Value;
                }

                if (inDependencies)
                {
                    // dep = "version" or dep = { version = "..." }
                    var depMatch = Regex.Match(trimmed, @"^([a-zA-Z0-9_\-]+)\s*=");
                    if (depMatch.Success)
                        dependencies.Add(depMatch.Groups[1].Value);
                }
            }

            if (name is null)
                return null;

            return new ProjectInfo(name, relativePath, "rust", dependencies, dependencies, []);
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
            var cargoToml = Path.Combine(dir, "Cargo.toml");
            if (File.Exists(cargoToml))
            {
                result.Add(cargoToml);
                // Don't return — Rust workspaces have nested Cargo.toml files
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                // Skip target directory (Rust build output)
                if (dirName == "target") continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
