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
                    if (TryReadQuotedName(trimmed, out var packageName))
                        name = packageName;
                }

                if (inDependencies)
                {
                    // dep = "version" or dep = { version = "..." }
                    if (TryReadDependencyKey(trimmed, out var dependency))
                        dependencies.Add(dependency);
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

    private static bool TryReadQuotedName(string text, out string name)
    {
        name = "";
        const string keyword = "name";
        if (!text.StartsWith(keyword, StringComparison.Ordinal))
            return false;

        var index = keyword.Length;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] != '=')
            return false;

        index++;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] != '"')
            return false;

        index++;
        var nameStart = index;
        while (index < text.Length && text[index] != '"')
            index++;

        if (index == nameStart || index >= text.Length)
            return false;

        name = text[nameStart..index];
        return true;
    }

    private static bool TryReadDependencyKey(string text, out string key)
    {
        key = "";
        var index = 0;
        while (index < text.Length && IsDependencyKeyCharacter(text[index]))
            index++;

        if (index == 0)
            return false;

        var keyEnd = index;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] != '=')
            return false;

        key = text[..keyEnd];
        return true;
    }

    private static bool IsDependencyKeyCharacter(char ch) =>
        (ch >= 'a' && ch <= 'z') ||
        (ch >= 'A' && ch <= 'Z') ||
        (ch >= '0' && ch <= '9') ||
        ch is '_' or '-';

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
