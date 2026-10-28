using System.Text.Json;
using System.Text.RegularExpressions;
using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>Package-manifest dependencies and compiler-defined TypeScript project membership.</summary>
public static class JavaScriptProjectDiscovery
{
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs,
        string? toolchainSearchPath = null)
    {
        var paths = new List<string>();
        CollectManifests(Path.GetFullPath(rootPath), excludedDirs, paths);
        paths.Sort(StringComparer.Ordinal);
        var packages = paths.Where(p => Path.GetFileName(p) == "package.json")
            .Select(p => ParsePackageJson(p, rootPath)).OfType<ProjectInfo>().ToList();
        foreach (var workspace in packages.Where(p => p.Properties.ContainsKey("WorkspacePatterns")))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(workspace.Path, rootPath))!;
            var patterns = JsonSerializer.Deserialize<string[]>(workspace.Properties["WorkspacePatterns"])!;
            foreach (var package in packages)
            {
                var memberDirectory = Path.GetDirectoryName(Path.GetFullPath(package.Path, rootPath))!;
                var relative = Path.GetRelativePath(directory, memberDirectory).Replace('\\', '/');
                if (relative != "." && !relative.StartsWith("../", StringComparison.Ordinal) && MatchesWorkspace(relative, patterns))
                    package.Properties["WorkspaceRoot"] = workspace.Name;
            }
        }

        var result = new List<ProjectInfo>(packages);
        var compiler = new TypeScriptProjection(toolchainSearchPath);
        var configurations = paths.Where(p => Path.GetFileName(p) != "package.json").ToList();
        var names = configurations.ToDictionary(path => Path.GetFullPath(path), path =>
        {
            var directory = Path.GetDirectoryName(path)!;
            var package = packages.FirstOrDefault(p =>
                Path.GetDirectoryName(Path.GetFullPath(p.Path, rootPath)) == directory);
            return (package?.Name ?? Path.GetRelativePath(rootPath, directory).Replace('\\', '/')) +
                ":" + Path.GetFileNameWithoutExtension(path);
        }, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in configurations)
        {
            var config = compiler.ReadProject(path);
            if (config is null)
            {
                ProviderWarnings.ReportOnce("javascript", "typescript-projects",
                    "TypeScript compiler unavailable; tsconfig/jsconfig membership, project references and path aliases are unknown. " +
                    "Package-manifest dependency analysis remains available.");
                continue;
            }
            foreach (var error in config.Errors) ProviderErrors.Report($"{path}: {error}");
            result.Add(new(names[path], Path.GetRelativePath(rootPath, path).Replace('\\', '/'), "javascript",
                config.References.Select(reference => names.GetValueOrDefault(Path.GetFullPath(reference)) ??
                    Path.GetRelativePath(rootPath, reference).Replace('\\', '/')).ToList(), [], [])
            {
                SourceFiles = config.SourceFiles.Select(Path.GetFullPath).ToList(),
                Properties = new()
                {
                    ["DiscoveryScope"] = "TypeScript compiler config (include/exclude/files/extends/references)",
                    ["PathAliases"] = JsonSerializer.Serialize(config.PathAliases),
                    ["BaseUrl"] = config.BaseUrl ?? ""
                }
            });
        }
        return result;
    }

    private static ProjectInfo? ParsePackageJson(string filePath, string rootPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString())) return null;
            var dependencies = new SortedSet<string>(StringComparer.Ordinal);
            var properties = new Dictionary<string, string>
            {
                ["DiscoveryScope"] = "package.json declared dependencies (runtime/development/peer/optional); no lockfile resolution"
            };
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (!root.TryGetProperty(section, out var value)) continue;
                if (value.ValueKind != JsonValueKind.Object)
                    throw new JsonException($"'{section}' must be an object.");
                foreach (var property in value.EnumerateObject()) dependencies.Add(property.Name);
                properties[section] = value.GetRawText();
            }
            if (root.TryGetProperty("workspaces", out var workspaces))
            {
                if (workspaces.ValueKind == JsonValueKind.Object && workspaces.TryGetProperty("packages", out var patterns))
                    workspaces = patterns;
                if (workspaces.ValueKind != JsonValueKind.Array ||
                    workspaces.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
                    throw new JsonException("'workspaces' must be a string array or an object with a 'packages' array.");
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
        foreach (var pattern in patterns)
        {
            bool exclude = pattern.StartsWith('!');
            var glob = (exclude ? pattern[1..] : pattern).TrimEnd('/').Replace('\\', '/');
            var regex = "^" + Regex.Escape(glob).Replace(@"\*\*/", "(?:.*/)?")
                .Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
            if (Regex.IsMatch(relative, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                matches = !exclude;
        }
        return matches;
    }

    private static void CollectManifests(string directory, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var name = Path.GetFileName(path);
                if (name == "package.json" || name == "jsconfig.json" || name == "tsconfig.json" ||
                    name.StartsWith("tsconfig.", StringComparison.Ordinal) || name.StartsWith("jsconfig.", StringComparison.Ordinal))
                    result.Add(path);
            }
            foreach (var subdirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(subdirectory);
                if (name is "node_modules" or ".git" || excluded?.Contains(name) == true ||
                    File.GetAttributes(subdirectory).HasFlag(FileAttributes.ReparsePoint)) continue;
                CollectManifests(subdirectory, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ProviderErrors.Report($"{directory}: Cannot discover JavaScript projects: {ex.Message}");
        }
    }
}
