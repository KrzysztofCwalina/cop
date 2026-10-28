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
            {
                var directory = Path.GetDirectoryName(manifestPath);
                while (directory is not null && Path.GetRelativePath(rootPath, directory) is var relative
                       && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                {
                    string workspace = Path.Combine(directory, "go.work");
                    if (File.Exists(workspace))
                    {
                        info.Properties["Workspace"] = Path.GetRelativePath(rootPath, workspace).Replace('\\', '/');
                        ReadContext(workspace, info.Properties, "Workspace");
                        break;
                    }
                    directory = Path.GetDirectoryName(directory);
                }
                result.Add(info);
            }
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
                var trimmed = StripComment(line).Trim();

                // module declaration
                if (TryReadTokenAfterKeyword(trimmed, "module", requireWhitespaceAfterToken: false, out var modulePath))
                {
                    moduleName = modulePath;
                    continue;
                }

                // require block
                if (trimmed.StartsWith("require", StringComparison.Ordinal) && trimmed["require".Length..].Trim() == "(")
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

            var properties = new Dictionary<string, string> { ["Module"] = moduleName.Trim('"') };
            ReadContext(filePath, properties, "");
            return new ProjectInfo(name.Trim('"'), relativePath, "go", dependencies.Distinct().ToList(), dependencies.Distinct().ToList(), [])
            { Properties = properties };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cop.Core.ProviderErrors.Report($"{filePath}: cannot read Go module: {ex.Message}");
            return null;
        }
    }

    private static void ReadContext(string filePath, Dictionary<string, string> properties, string prefix)
    {
        try
        {
            string? block = null;
            var values = new Dictionary<string, List<string>>();
            foreach (string line in File.ReadLines(filePath))
            {
                string text = StripComment(line).Trim();
                if (text.Length == 0) continue;
                if (text == ")") { block = null; continue; }
                string directive;
                string value;
                if (block is not null) { directive = block; value = text; }
                else
                {
                    int space = text.IndexOfAny([' ', '\t']);
                    if (space < 0) continue;
                    directive = text[..space];
                    value = text[(space + 1)..].Trim();
                    if (value == "(") { block = directive; continue; }
                }
                string? key = directive switch
                {
                    "go" => "GoVersion",
                    "toolchain" => "Toolchain",
                    "replace" => "Replacements",
                    "exclude" => "Exclusions",
                    "use" => "Uses",
                    _ => null
                };
                if (key is null) continue;
                if (!values.TryGetValue(key, out var list)) values[key] = list = [];
                list.Add(value);
            }
            foreach (var (key, value) in values) properties[prefix + key] = string.Join("\n", value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cop.Core.ProviderErrors.Report($"{filePath}: cannot read Go workspace/module context: {ex.Message}");
        }
    }

    private static string StripComment(string text)
    {
        bool quoted = false, escaped = false;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '"' && !escaped) quoted = !quoted;
            if (!quoted && text[i] == '/' && text[i + 1] == '/') return text[..i];
            escaped = text[i] == '\\' && !escaped;
        }
        return text;
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
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                if (dirName == "vendor") continue;
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Cop.Core.ProviderErrors.Report($"{dir}: cannot discover Go modules: {ex.Message}");
        }
    }
}
