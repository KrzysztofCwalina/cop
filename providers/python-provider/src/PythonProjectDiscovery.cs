using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Discovers Python projects by scanning for pyproject.toml and setup.py files,
/// extracting project name and dependencies.
/// </summary>
public static class PythonProjectDiscovery
{
    /// <summary>
    /// Discovers Python projects under rootPath by finding pyproject.toml files.
    /// </summary>
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs)
    {
        var manifestPaths = new List<string>();
        CollectManifests(rootPath, excludedDirs, manifestPaths);

        var result = new List<ProjectInfo>();
        foreach (var manifestPath in manifestPaths)
        {
            var info = ParseManifest(manifestPath, rootPath);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    private static ProjectInfo? ParseManifest(string manifestPath, string rootPath)
    {
        var fileName = Path.GetFileName(manifestPath);
        var relativePath = Path.GetRelativePath(rootPath, manifestPath).Replace('\\', '/');

        if (fileName == "pyproject.toml")
            return ParsePyprojectToml(manifestPath, relativePath);
        if (fileName == "setup.py")
            return ParseSetupPy(manifestPath, relativePath);

        return null;
    }

    private static ProjectInfo? ParsePyprojectToml(string filePath, string relativePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            string? name = null;
            var dependencies = new List<string>();
            bool inProject = false;
            bool inDependencies = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Track sections
                if (trimmed.StartsWith("["))
                {
                    inProject = trimmed == "[project]";
                    inDependencies = trimmed == "[project]" && inDependencies
                        || trimmed == "[project.dependencies]";
                    if (trimmed != "[project]" && trimmed != "[project.dependencies]")
                    {
                        inProject = false;
                        inDependencies = false;
                    }
                    continue;
                }

                if (inProject)
                {
                    // name = "mypackage"
                    var parsedName = ParsePyprojectName(trimmed);
                    if (parsedName is not null)
                        name = parsedName;

                    // dependencies = ["dep1", "dep2>=1.0"]
                    if (trimmed.StartsWith("dependencies"))
                    {
                        inDependencies = true;
                        var inlineDependencies = ParseInlineDependencies(trimmed);
                        if (inlineDependencies is not null)
                        {
                            ParseDependencyList(inlineDependencies, dependencies);
                            inDependencies = false;
                        }
                    }
                    else if (inDependencies)
                    {
                        if (trimmed == "]")
                        {
                            inDependencies = false;
                        }
                        else
                        {
                            var dependency = ParseDoubleQuotedValue(trimmed);
                            if (dependency is not null)
                                dependencies.Add(NormalizePythonDep(dependency));
                        }
                    }
                }
            }

            if (name is null)
                return null;

            return new ProjectInfo(name, relativePath, "python", dependencies, dependencies, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static ProjectInfo? ParseSetupPy(string filePath, string relativePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var name = ParseSetupName(content);
            if (name is null)
                return null;

            var dependencies = new List<string>();

            var installRequires = ParseInstallRequires(content);
            if (installRequires is not null)
                ParseDependencyList(installRequires, dependencies);

            return new ProjectInfo(name, relativePath, "python", dependencies, dependencies, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void ParseDependencyList(string text, List<string> dependencies)
    {
        foreach (var dependency in ParseQuotedValues(text))
            dependencies.Add(NormalizePythonDep(dependency));
    }

    /// <summary>
    /// Strips version specifiers from a dependency string (e.g., "requests>=2.0" → "requests").
    /// </summary>
    private static string NormalizePythonDep(string dep)
    {
        var length = 0;
        while (length < dep.Length && IsDependencyNameChar(dep[length]))
            length++;
        return length > 0 ? dep[..length] : dep;
    }

    private static string? ParsePyprojectName(string text)
    {
        var index = "name".Length;
        if (!text.StartsWith("name", StringComparison.Ordinal))
            return null;

        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '=')
            return null;
        index++;
        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '"')
            return null;

        return ReadUntilQuote(text, index + 1, '"', allowEitherQuote: false);
    }

    private static string? ParseInlineDependencies(string text)
    {
        var index = text.IndexOf("dependencies", StringComparison.Ordinal);
        if (index < 0)
            return null;

        index += "dependencies".Length;
        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '=')
            return null;
        index++;
        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '[')
            return null;

        var end = text.LastIndexOf(']');
        if (end <= index + 1)
            return null;

        return text[(index + 1)..end];
    }

    private static string? ParseDoubleQuotedValue(string text)
    {
        var start = text.IndexOf('"');
        if (start < 0)
            return null;
        return ReadUntilQuote(text, start + 1, '"', allowEitherQuote: false);
    }

    private static string? ParseSetupName(string text)
    {
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var index = text.IndexOf("name", searchIndex, StringComparison.Ordinal);
            if (index < 0)
                return null;

            var valueStart = index + "name".Length;
            valueStart = SkipWhitespace(text, valueStart);
            if (valueStart < text.Length && text[valueStart] == '=')
            {
                valueStart++;
                valueStart = SkipWhitespace(text, valueStart);
                if (valueStart < text.Length && IsQuote(text[valueStart]))
                {
                    var value = ReadUntilQuote(text, valueStart + 1, text[valueStart], allowEitherQuote: true);
                    if (value is not null)
                        return value;
                }
            }

            searchIndex = index + 1;
        }

        return null;
    }

    private static string? ParseInstallRequires(string text)
    {
        var index = text.IndexOf("install_requires", StringComparison.Ordinal);
        if (index < 0)
            return null;

        index += "install_requires".Length;
        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '=')
            return null;
        index++;
        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '[')
            return null;

        var start = index + 1;
        var end = text.IndexOf(']', start);
        return end >= 0 ? text[start..end] : null;
    }

    private static IEnumerable<string> ParseQuotedValues(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (!IsQuote(text[index]))
            {
                index++;
                continue;
            }

            var start = index + 1;
            var end = start;
            while (end < text.Length && !IsQuote(text[end]))
                end++;

            if (end < text.Length && end > start)
            {
                yield return text[start..end];
                index = end + 1;
            }
            else
            {
                index++;
            }
        }
    }

    private static string? ReadUntilQuote(string text, int start, char quote, bool allowEitherQuote)
    {
        var end = start;
        while (end < text.Length && (allowEitherQuote ? !IsQuote(text[end]) : text[end] != quote))
            end++;

        return end < text.Length && end > start ? text[start..end] : null;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static bool IsQuote(char ch) => ch is '\'' or '"';

    private static bool IsDependencyNameChar(char ch) =>
        ch is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-'
            or '.';

    private static void CollectManifests(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            var pyproject = Path.Combine(dir, "pyproject.toml");
            if (File.Exists(pyproject))
            {
                result.Add(pyproject);
                return; // Don't recurse into sub-packages of a project
            }

            var setupPy = Path.Combine(dir, "setup.py");
            if (File.Exists(setupPy))
            {
                result.Add(setupPy);
                return;
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
