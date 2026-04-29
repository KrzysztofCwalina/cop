using System.Text.RegularExpressions;
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
                    var nameMatch = Regex.Match(trimmed, @"^name\s*=\s*""([^""]+)""");
                    if (nameMatch.Success)
                        name = nameMatch.Groups[1].Value;

                    // dependencies = ["dep1", "dep2>=1.0"]
                    if (trimmed.StartsWith("dependencies"))
                    {
                        inDependencies = true;
                        var inlineMatch = Regex.Match(trimmed, @"dependencies\s*=\s*\[(.+)\]");
                        if (inlineMatch.Success)
                        {
                            ParseDependencyList(inlineMatch.Groups[1].Value, dependencies);
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
                            var depMatch = Regex.Match(trimmed, @"""([^""]+)""");
                            if (depMatch.Success)
                                dependencies.Add(NormalizePythonDep(depMatch.Groups[1].Value));
                        }
                    }
                }
            }

            if (name is null)
                return null;

            return new ProjectInfo(name, relativePath, "python", dependencies);
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
            var nameMatch = Regex.Match(content, @"name\s*=\s*['""]([^'""]+)['""]");
            if (!nameMatch.Success)
                return null;

            var name = nameMatch.Groups[1].Value;
            var dependencies = new List<string>();

            var depsMatch = Regex.Match(content, @"install_requires\s*=\s*\[([^\]]*)\]", RegexOptions.Singleline);
            if (depsMatch.Success)
                ParseDependencyList(depsMatch.Groups[1].Value, dependencies);

            return new ProjectInfo(name, relativePath, "python", dependencies);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void ParseDependencyList(string text, List<string> dependencies)
    {
        var matches = Regex.Matches(text, @"['""]([^'""]+)['""]");
        foreach (Match m in matches)
            dependencies.Add(NormalizePythonDep(m.Groups[1].Value));
    }

    /// <summary>
    /// Strips version specifiers from a dependency string (e.g., "requests>=2.0" → "requests").
    /// </summary>
    private static string NormalizePythonDep(string dep)
    {
        var match = Regex.Match(dep, @"^([a-zA-Z0-9_\-\.]+)");
        return match.Success ? match.Groups[1].Value : dep;
    }

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
