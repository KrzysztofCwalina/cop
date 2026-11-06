// Discovers JavaScript package dependencies and TypeScript project relationships.

using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>Package-manifest dependencies and compiler-defined TypeScript project membership.</summary>
public static class JavaScriptProjectDiscovery
{
    /// <summary>
    /// Discovers JavaScript projects and their package or compiler references.
    /// </summary>
    public static async ValueTask<List<ProjectInfo>> DiscoverAsync(
        string rootPath, IReadOnlySet<string>? excludedDirs,
        string? toolchainSearchPath = null, CancellationToken cancellationToken = default)
    {
        List<string> paths = new List<string>();
        CollectManifests(Path.GetFullPath(rootPath), excludedDirs, paths);
        paths.Sort(StringComparer.Ordinal);
        List<ProjectInfo> packages = paths.Where(p => Path.GetFileName(p) == "package.json")
            .Select(p => ParsePackageJson(p, rootPath)).OfType<ProjectInfo>().ToList();
        foreach (ProjectInfo? workspace in packages.Where(p => p.Properties.ContainsKey("WorkspacePatterns")))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(workspace.Path, rootPath))!;
            string[] patterns = JsonSerializer.Deserialize<string[]>(workspace.Properties["WorkspacePatterns"])!;
            foreach (ProjectInfo? package in packages)
            {
                string memberDirectory = Path.GetDirectoryName(Path.GetFullPath(package.Path, rootPath))!;
                string relative = Path.GetRelativePath(directory, memberDirectory).Replace('\\', '/');
                if (relative != "." && !relative.StartsWith("../", StringComparison.Ordinal) && MatchesWorkspace(relative, patterns))
                {
                    package.Properties["WorkspaceRoot"] = workspace.Name;
                }
            }
        }

        List<ProjectInfo> result = new List<ProjectInfo>(packages);
        TypeScriptProjection compiler = new TypeScriptProjection(toolchainSearchPath);
        List<string> configurations = paths.Where(p => Path.GetFileName(p) != "package.json").ToList();
        Dictionary<string, string> names = configurations.ToDictionary(path => Path.GetFullPath(path), path =>
        {
            string directory = Path.GetDirectoryName(path)!;
            ProjectInfo? package = packages.FirstOrDefault(p =>
                Path.GetDirectoryName(Path.GetFullPath(p.Path, rootPath)) == directory);
            return (package?.Name ?? Path.GetRelativePath(rootPath, directory).Replace('\\', '/')) +
                ":" + Path.GetFileNameWithoutExtension(path);
        }, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string path in configurations)
        {
            TypeScriptProjection.ProjectConfiguration? config =
                await compiler.ReadProjectAsync(path, cancellationToken).ConfigureAwait(false);
            if (config is null)
            {
                ProviderWarnings.ReportOnce("javascript", "typescript-projects",
                    "TypeScript compiler unavailable; tsconfig/jsconfig membership, project references and path aliases are unknown. " +
                    "Package-manifest dependency analysis remains available.");
                continue;
            }
            foreach (string error in config.Errors)
            {
                ProviderErrors.Report($"{path}: {error}");
            }

            result.Add(new(names[path], Path.GetRelativePath(rootPath, path).Replace('\\', '/'), "javascript",
                config.References.Select(reference => names.GetValueOrDefault(Path.GetFullPath(reference)) ??
                    Path.GetRelativePath(rootPath, reference).Replace('\\', '/')).ToList(), [], [])
            {
                SourceFiles = config.SourceFiles.Select(Path.GetFullPath).ToList(),
                Properties = new()
                {
                    ["DiscoveryScope"] = "TypeScript compiler config (include/exclude/files/extends/references)",
                    ["PathAliases"] = JsonSerializer.Serialize(config.PathAliases),
                    ["BaseUrl"] = config.BaseUrl ?? string.Empty
                }
            });
        }
        return result;
    }

    private static ProjectInfo? ParsePackageJson(string filePath, string rootPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(filePath));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                return null;
            }

            SortedSet<string> dependencies = new SortedSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> properties = new Dictionary<string, string>
            {
                ["DiscoveryScope"] = "package.json declared dependencies (runtime/development/peer/optional); no lockfile resolution"
            };
            foreach (string? section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (!root.TryGetProperty(section, out JsonElement value))
                {
                    continue;
                }

                if (value.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException($"'{section}' must be an object.");
                }

                foreach (JsonProperty property in value.EnumerateObject())
                {
                    dependencies.Add(property.Name);
                }

                properties[section] = value.GetRawText();
            }
            if (root.TryGetProperty("workspaces", out JsonElement workspaces))
            {
                if (workspaces.ValueKind == JsonValueKind.Object && workspaces.TryGetProperty("packages", out JsonElement patterns))
                {
                    workspaces = patterns;
                }

                if (workspaces.ValueKind != JsonValueKind.Array ||
                    workspaces.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
                {
                    throw new JsonException("'workspaces' must be a string array or an object with a 'packages' array.");
                }

                properties["WorkspacePatterns"] = workspaces.GetRawText();
            }
            return new(nameElement.GetString()!, Path.GetRelativePath(rootPath, filePath).Replace('\\', '/'),
                "javascript", dependencies.ToList(), dependencies.ToList(), []) { Properties = properties };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ProviderErrors.Report($"{filePath}: Cannot read JavaScript package manifest: {ex.Message}");
            return null;
        }
    }

    private static bool MatchesWorkspace(string relative, IEnumerable<string> patterns)
    {
        bool matches = false;
        foreach (string pattern in patterns)
        {
            bool exclude = pattern.StartsWith('!');
            string glob = (exclude ? pattern[1..] : pattern).TrimEnd('/').Replace('\\', '/');
            if (MatchesGlob(relative, glob))
            {
                matches = !exclude;
            }
        }
        return matches;
    }

    private static bool MatchesGlob(string path, string pattern)
    {
        // Dynamic programming bounds work by path length times pattern length, without backtracking.
        bool[] next = new bool[path.Length + 1];
        next[path.Length] = true;
        for (int i = pattern.Length - 1; i >= 0; i--)
        {
            bool recursiveDirectory = pattern[i] == '/' && i >= 2 && pattern[i - 1] == '*' && pattern[i - 2] == '*';
            bool recursive = !recursiveDirectory && pattern[i] == '*' && i > 0 && pattern[i - 1] == '*';
            bool[] current = new bool[path.Length + 1];
            bool directorySuffix = false;
            for (int j = path.Length; j >= 0; j--)
            {
                if (recursiveDirectory)
                {
                    if (j < path.Length && path[j] == '/' && next[j + 1])
                    {
                        directorySuffix = true;
                    }

                    current[j] = next[j] || directorySuffix;
                }
                else if (pattern[i] == '*')
                {
                    current[j] = next[j] || j < path.Length && (recursive || path[j] != '/') && current[j + 1];
                }
                else if (j < path.Length)
                {
                    current[j] = (pattern[i] == '?' ? path[j] != '/' : pattern[i] == path[j]) && next[j + 1];
                }
            }
            if (recursiveDirectory)
            {
                i -= 2;
            }
            else if (recursive)
            {
                i--;
            }

            next = current;
        }
        return next[0];
    }

    private static void CollectManifests(string directory, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                string name = Path.GetFileName(path);
                if (name == "package.json" || name == "jsconfig.json" || name == "tsconfig.json" ||
                    name.StartsWith("tsconfig.", StringComparison.Ordinal) || name.StartsWith("jsconfig.", StringComparison.Ordinal))
                {
                    result.Add(path);
                }
            }
            foreach (string? subdirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(subdirectory);
                if (name is "node_modules" or ".git" || excluded?.Contains(name) == true ||
                    File.GetAttributes(subdirectory).HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                CollectManifests(subdirectory, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ProviderErrors.Report($"{directory}: Cannot discover JavaScript projects: {ex.Message}");
        }
    }
}
