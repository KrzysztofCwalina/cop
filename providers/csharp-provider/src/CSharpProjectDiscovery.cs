using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Discovers C# projects by scanning for .csproj files and extracting
/// project name and ProjectReference dependencies.
/// </summary>
public static class CSharpProjectDiscovery
{
    /// <summary>
    /// Discovers all .csproj projects under rootPath, resolving ProjectReference
    /// targets by path to produce stable project-to-project edges.
    /// </summary>
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs)
    {
        var csprojPaths = new List<string>();
        CollectCsprojFiles(rootPath, excludedDirs, csprojPaths);

        // First pass: build a map of normalized path → project name
        var pathToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<(string Name, string RelativePath, List<string> RefPaths)>();

        foreach (var csprojPath in csprojPaths)
        {
            var (name, refPaths) = ParseCsproj(csprojPath);
            var relativePath = Path.GetRelativePath(rootPath, csprojPath).Replace('\\', '/');
            pathToName[Path.GetFullPath(csprojPath)] = name;
            projects.Add((name, relativePath, refPaths));
        }

        // Second pass: resolve ProjectReference paths to project names
        var result = new List<ProjectInfo>();
        foreach (var (name, relativePath, refPaths) in projects)
        {
            var references = new List<string>();
            var csprojDir = Path.GetDirectoryName(Path.Combine(rootPath, relativePath.Replace('/', '\\'))) ?? rootPath;

            foreach (var refPath in refPaths)
            {
                var resolvedPath = Path.GetFullPath(Path.Combine(csprojDir, refPath.Replace('/', '\\')));
                if (pathToName.TryGetValue(resolvedPath, out var targetName))
                    references.Add(targetName);
                else
                    references.Add(Path.GetFileNameWithoutExtension(refPath));
            }

            result.Add(new ProjectInfo(name, relativePath, "csharp", references));
        }

        return result;
    }

    private static (string Name, List<string> RefPaths) ParseCsproj(string csprojPath)
    {
        string name = Path.GetFileNameWithoutExtension(csprojPath);
        var refPaths = new List<string>();

        try
        {
            var doc = XDocument.Load(csprojPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // Try to get AssemblyName; fall back to filename
            var assemblyName = doc.Root?.Descendants(ns + "AssemblyName").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(assemblyName))
                name = assemblyName;

            // Extract ProjectReference Include paths
            foreach (var projRef in doc.Root?.Descendants(ns + "ProjectReference") ?? [])
            {
                var include = projRef.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                    refPaths.Add(include);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Malformed csproj — return what we have
        }

        return (name, refPaths);
    }

    private static void CollectCsprojFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir, "*.csproj"))
                result.Add(file);

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectCsprojFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
