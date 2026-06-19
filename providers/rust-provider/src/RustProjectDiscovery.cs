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
                    inDependencies = false;

                    var header = trimmed.TrimStart('[').TrimEnd(']').Trim();

                    // Sub-table dependency, e.g. [dependencies.serde],
                    // [build-dependencies.cc] or [target.'cfg(unix)'.dependencies.nix]
                    // declares a dependency on the trailing name; its keys are that
                    // crate's properties, not new dependencies.
                    if (TryReadSubTableDependency(header, out var subDep))
                    {
                        dependencies.Add(subDep);
                    }
                    // Plain dependency table, including target-specific variants like
                    // [target.'cfg(windows)'.dependencies].
                    else if (header == "dependencies" || header == "dev-dependencies"
                        || header == "build-dependencies" || header.EndsWith(".dependencies"))
                    {
                        inDependencies = true;
                    }
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

        // Dotted single-line form: `dep.workspace = true`, `dep.path = "..."`,
        // `dep.version = "1.0"`. The dependency is on `dep`.
        if (index < text.Length && text[index] == '.')
        {
            key = text[..keyEnd];
            return true;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] != '=')
            return false;

        key = text[..keyEnd];
        return true;
    }

    /// <summary>
    /// Recognizes a dependency sub-table header body (brackets already stripped), e.g.
    /// "dependencies.serde", "dev-dependencies.tokio", or
    /// "target.'cfg(unix)'.dependencies.nix", and extracts the trailing crate name.
    /// </summary>
    private static bool TryReadSubTableDependency(string header, out string name)
    {
        name = "";
        const string marker = "dependencies.";
        int idx = header.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return false;

        var remainder = header[(idx + marker.Length)..].Trim();
        if (remainder.Length == 0)
            return false;

        foreach (var ch in remainder)
        {
            if (!IsDependencyKeyCharacter(ch))
                return false;
        }

        name = remainder;
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
