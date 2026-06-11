using System.Text.RegularExpressions;
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
            var nameMatch = Regex.Match(content, @"<artifactId>([^<]+)</artifactId>");
            if (!nameMatch.Success) return null;
            var name = nameMatch.Groups[1].Value;

            // Extract dependencies
            var dependencies = new List<string>();
            var depMatches = Regex.Matches(content, @"<dependency>\s*<groupId>([^<]+)</groupId>\s*<artifactId>([^<]+)</artifactId>", RegexOptions.Singleline);
            foreach (Match m in depMatches)
                dependencies.Add($"{m.Groups[1].Value}:{m.Groups[2].Value}");

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
            var depMatches = Regex.Matches(content, @"(?:implementation|api|compile|testImplementation)\s+['""]([^'""]+)['""]");
            foreach (Match m in depMatches)
            {
                var parts = m.Groups[1].Value.Split(':');
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
