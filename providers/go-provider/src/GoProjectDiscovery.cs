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
                if (TryReadTokenAfterKeyword(trimmed, "module", requireWhitespaceAfterToken: false, out var modulePath))
                {
                    moduleName = modulePath;
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
                if (TryReadTokenAfterKeyword(trimmed, "require", requireWhitespaceAfterToken: true, out var dependency))
                {
                    dependencies.Add(dependency);
                    continue;
                }

                if (inRequire)
                {
                    if (TryReadFirstTokenBeforeWhitespace(trimmed, out var blockDependency) && !trimmed.StartsWith("//"))
                        dependencies.Add(blockDependency);
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

    private static bool TryReadTokenAfterKeyword(string text, string keyword, bool requireWhitespaceAfterToken, out string token)
    {
        token = "";
        if (!text.StartsWith(keyword, StringComparison.Ordinal))
            return false;

        var index = keyword.Length;
        if (index >= text.Length || !char.IsWhiteSpace(text[index]))
            return false;

        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        var tokenStart = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
            index++;

        if (index == tokenStart)
            return false;

        if (requireWhitespaceAfterToken && (index >= text.Length || !char.IsWhiteSpace(text[index])))
            return false;

        token = text[tokenStart..index];
        return true;
    }

    private static bool TryReadFirstTokenBeforeWhitespace(string text, out string token)
    {
        token = "";
        var index = 0;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
            index++;

        if (index == 0 || index >= text.Length)
            return false;

        token = text[..index];
        return true;
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
