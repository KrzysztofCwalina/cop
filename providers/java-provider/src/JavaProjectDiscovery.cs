using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Discovers Java projects by scanning for pom.xml and build.gradle files,
/// extracting project name and dependencies.
/// </summary>
public static class JavaProjectDiscovery
{
    /// <summary>
    /// Discovers Java projects under rootPath by finding pom.xml or build.gradle files.
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

    private static ProjectInfo? ParseManifest(string filePath, string rootPath)
    {
        var fileName = Path.GetFileName(filePath);
        var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

        if (fileName == "pom.xml")
            return ParsePomXml(filePath, relativePath);
        if (fileName is "build.gradle" or "build.gradle.kts")
            return ParseBuildGradle(filePath, relativePath);

        return null;
    }

    private static ProjectInfo? ParsePomXml(string filePath, string relativePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);

            // Extract artifactId as name
            if (!TryFindElementContent(content, "<artifactId>", "</artifactId>", 0, out var name, out _))
                return null;

            // Extract dependencies
            var dependencies = new List<string>();
            foreach (var dependency in ReadPomDependencies(content))
                dependencies.Add(dependency);

            return new ProjectInfo(name, relativePath, "java", dependencies, dependencies, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static ProjectInfo? ParseBuildGradle(string filePath, string relativePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);

            // Use directory name as project name
            var name = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "unknown";

            // Extract dependencies: implementation 'group:artifact:version'
            var dependencies = new List<string>();
            foreach (var dependency in ReadGradleDependencies(content))
            {
                var parts = dependency.Split(':');
                if (parts.Length >= 2)
                    dependencies.Add($"{parts[0]}:{parts[1]}");
            }

            return new ProjectInfo(name, relativePath, "java", dependencies, dependencies, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool TryFindElementContent(string text, string startTag, string endTag, int startIndex, out string value, out int endIndex)
    {
        value = "";
        endIndex = startIndex;

        var searchIndex = startIndex;
        while (searchIndex < text.Length)
        {
            var tagIndex = text.IndexOf(startTag, searchIndex, StringComparison.Ordinal);
            if (tagIndex < 0)
                return false;

            var valueStart = tagIndex + startTag.Length;
            var valueEnd = text.IndexOf(endTag, valueStart, StringComparison.Ordinal);
            if (valueEnd > valueStart && text.IndexOf('<', valueStart, valueEnd - valueStart) < 0)
            {
                value = text[valueStart..valueEnd];
                endIndex = valueEnd + endTag.Length;
                return true;
            }

            searchIndex = tagIndex + 1;
        }

        return false;
    }

    private static IEnumerable<string> ReadPomDependencies(string content)
    {
        var searchIndex = 0;
        const string dependencyTag = "<dependency>";
        while (searchIndex < content.Length)
        {
            var dependencyIndex = content.IndexOf(dependencyTag, searchIndex, StringComparison.Ordinal);
            if (dependencyIndex < 0)
                yield break;

            var index = dependencyIndex + dependencyTag.Length;
            index = SkipWhitespace(content, index);

            if (TryReadElementContentAt(content, "<groupId>", "</groupId>", index, out var groupId, out index))
            {
                index = SkipWhitespace(content, index);
                if (TryReadElementContentAt(content, "<artifactId>", "</artifactId>", index, out var artifactId, out var endIndex))
                {
                    yield return $"{groupId}:{artifactId}";
                    searchIndex = Math.Max(endIndex, dependencyIndex + 1);
                }
                else
                {
                    searchIndex = Math.Max(index, dependencyIndex + 1);
                }

            }
            else
            {
                searchIndex = dependencyIndex + 1;
            }
        }
    }

    private static bool TryReadElementContentAt(string text, string startTag, string endTag, int startIndex, out string value, out int endIndex)
    {
        value = "";
        endIndex = startIndex;

        if (!text.AsSpan(startIndex).StartsWith(startTag, StringComparison.Ordinal))
            return false;

        var valueStart = startIndex + startTag.Length;
        var valueEnd = text.IndexOf(endTag, valueStart, StringComparison.Ordinal);
        if (valueEnd < 0 || valueEnd == valueStart || text.IndexOf('<', valueStart, valueEnd - valueStart) >= 0)
            return false;

        value = text[valueStart..valueEnd];
        endIndex = valueEnd + endTag.Length;
        return true;
    }

    private static IEnumerable<string> ReadGradleDependencies(string content)
    {
        var index = 0;
        while (index < content.Length)
        {
            if (!TryReadGradleDependency(content, index, out var dependency, out var nextIndex))
            {
                index++;
                continue;
            }

            yield return dependency;
            index = nextIndex;
        }
    }

    private static bool TryReadGradleDependency(string text, int startIndex, out string dependency, out int nextIndex)
    {
        dependency = "";
        nextIndex = startIndex;

        var keywordLength = GetGradleDependencyKeywordLength(text, startIndex);
        if (keywordLength == 0)
            return false;

        var index = startIndex + keywordLength;
        if (index >= text.Length || !char.IsWhiteSpace(text[index]))
            return false;

        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] is not ('\'' or '"'))
            return false;

        index++;
        var dependencyStart = index;
        while (index < text.Length && text[index] is not ('\'' or '"'))
            index++;

        if (index == dependencyStart || index >= text.Length)
            return false;

        dependency = text[dependencyStart..index];
        nextIndex = index + 1;
        return true;
    }

    private static int GetGradleDependencyKeywordLength(string text, int startIndex)
    {
        string[] keywords = ["implementation", "api", "compile", "testImplementation"];
        foreach (var keyword in keywords)
        {
            if (text.AsSpan(startIndex).StartsWith(keyword, StringComparison.Ordinal))
                return keyword.Length;
        }

        return 0;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static void CollectManifests(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            var pom = Path.Combine(dir, "pom.xml");
            if (File.Exists(pom))
            {
                result.Add(pom);
                return;
            }

            var gradle = Path.Combine(dir, "build.gradle");
            if (File.Exists(gradle))
            {
                result.Add(gradle);
                return;
            }

            var gradleKts = Path.Combine(dir, "build.gradle.kts");
            if (File.Exists(gradleKts))
            {
                result.Add(gradleKts);
                return;
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                if (dirName is "target" or "build" or ".gradle") continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
