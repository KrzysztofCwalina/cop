using Cop.Providers.SourceModel;
using System.Xml.Linq;
using Cop.Providers.SourceParsers;

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
            var root = XDocument.Load(filePath).Root;
            var name = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "artifactId")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
                return null;

            var dependencies = new List<string>();
            foreach (var dependency in root!.Elements().Where(e => e.Name.LocalName == "dependencies")
                         .SelectMany(e => e.Elements()).Where(e => e.Name.LocalName == "dependency"))
            {
                string? group = dependency.Elements().FirstOrDefault(e => e.Name.LocalName == "groupId")?.Value.Trim();
                string? artifact = dependency.Elements().FirstOrDefault(e => e.Name.LocalName == "artifactId")?.Value.Trim();
                if (!string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(artifact))
                    dependencies.Add($"{group}:{artifact}");
            }

            return new ProjectInfo(name, relativePath, "java", dependencies, dependencies, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Cop.Core.ProviderErrors.Report($"{filePath}: cannot read Maven project: {ex.Message}");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cop.Core.ProviderErrors.Report($"{filePath}: cannot read Gradle project: {ex.Message}");
            return null;
        }
    }

    internal static IEnumerable<string> ReadGradleDependencies(string content, IReadOnlySet<string>? selectedConfigurations = null)
    {
        var tokens = new JavaLexer(content).Tokenize(singleQuotedStrings: true);
        string[] configurations = ["implementation", "api", "compile", "compileOnly", "runtimeOnly", "testImplementation", "testRuntimeOnly", "annotationProcessor"];
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != JavaTokenKind.Identifier
                || !(selectedConfigurations?.Contains(tokens[i].Value) ?? configurations.Contains(tokens[i].Value))) continue;
            int valueIndex = i + 1;
            if (tokens[valueIndex].Value == "(") valueIndex++;
            if (valueIndex >= tokens.Count || tokens[valueIndex].Kind != JavaTokenKind.StringLiteral) continue;
            string value = tokens[valueIndex].Value;
            if (value.Length >= 2 && !value.Contains('$')) yield return value[1..^1];
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
            }

            var gradle = Path.Combine(dir, "build.gradle");
            if (!File.Exists(pom) && File.Exists(gradle))
            {
                result.Add(gradle);
            }

            var gradleKts = Path.Combine(dir, "build.gradle.kts");
            if (!File.Exists(pom) && !File.Exists(gradle) && File.Exists(gradleKts))
            {
                result.Add(gradleKts);
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                if (dirName is "target" or "build" or ".gradle") continue;
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Cop.Core.ProviderErrors.Report($"{dir}: cannot discover Java projects: {ex.Message}");
        }
    }
}
