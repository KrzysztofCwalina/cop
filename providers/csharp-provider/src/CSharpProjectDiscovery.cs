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
        var projects = new List<(string Name, string RelativePath, List<string> RefPaths, List<string> Packages, List<string> Frameworks, Dictionary<string, string> Properties)>();

        foreach (var csprojPath in csprojPaths)
        {
            var (name, refPaths, packages, frameworks, properties) = ParseCsproj(csprojPath);
            var relativePath = Path.GetRelativePath(rootPath, csprojPath).Replace('\\', '/');
            pathToName[Path.GetFullPath(csprojPath)] = name;
            projects.Add((name, relativePath, refPaths, packages, frameworks, properties));
        }

        // Second pass: resolve ProjectReference paths to project names
        var result = new List<ProjectInfo>();
        foreach (var (name, relativePath, refPaths, packages, frameworks, properties) in projects)
        {
            var references = new List<string>();
            var csprojDir = Path.GetDirectoryName(Path.Combine(rootPath, NormalizePath(relativePath))) ?? rootPath;

            foreach (var refPath in refPaths)
            {
                var resolvedPath = Path.GetFullPath(Path.Combine(csprojDir, NormalizePath(refPath)));
                if (pathToName.TryGetValue(resolvedPath, out var targetName))
                    references.Add(targetName);
                else
                    references.Add(Path.GetFileNameWithoutExtension(NormalizePath(refPath)));
            }

            result.Add(new ProjectInfo(name, relativePath, "csharp", references, packages, frameworks)
            {
                Properties = properties
            });
        }

        return result;
    }

    /// <summary>
    /// Property names to extract from PropertyGroup elements.
    /// </summary>
    private static readonly HashSet<string> PropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OutputType", "TargetFramework", "TargetFrameworks",
        "IsTestProject", "IsPackable", "RootNamespace",
        "Nullable", "ImplicitUsings", "LangVersion",
    };

    private static (string Name, List<string> RefPaths, List<string> Packages, List<string> Frameworks, Dictionary<string, string> Properties) ParseCsproj(string csprojPath)
    {
        string name = Path.GetFileNameWithoutExtension(csprojPath);
        var refPaths = new List<string>();
        var packages = new List<string>();
        var frameworks = new List<string>();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var doc = LoadProjectDocument(csprojPath);
            if (doc is null)
                return (name, refPaths, packages, frameworks, properties);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // Extract properties from PropertyGroup elements
            foreach (var propGroup in doc.Root?.Elements(ns + "PropertyGroup") ?? [])
            {
                foreach (var el in propGroup.Elements())
                {
                    var elName = el.Name.LocalName;
                    var elValue = el.Value;
                    if (!string.IsNullOrWhiteSpace(elValue) && PropertyNames.Contains(elName))
                    {
                        properties[elName] = elValue.Trim();
                        AddTargetFrameworks(frameworks, elName, elValue);
                    }
                }
            }

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

            // Extract PackageReference names
            foreach (var pkgRef in doc.Root?.Descendants(ns + "PackageReference") ?? [])
            {
                var include = pkgRef.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                    packages.Add(include);
            }

            // Extract FrameworkReference names
            foreach (var fwRef in doc.Root?.Descendants(ns + "FrameworkReference") ?? [])
            {
                var include = fwRef.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                    frameworks.Add(include);
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            Cop.Core.ProviderErrors.Report($"Failed to read project '{csprojPath}': {ex.Message}");
        }

        return (name, refPaths, packages, frameworks, properties);
    }

    internal static XDocument? LoadProjectDocument(string path)
    {
        using var stream = File.OpenRead(path);
        try
        {
            return XDocument.Load(stream);
        }
        catch (System.Xml.XmlException)
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            if (!string.IsNullOrWhiteSpace(reader.ReadToEnd()))
                throw;

            Cop.Core.ProviderWarnings.ReportOnce("csharp", Path.GetFullPath(path),
                $"Project '{path}' is empty; analyzing its C# sources without project metadata.");
            return null;
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static void AddTargetFrameworks(List<string> frameworks, string propertyName, string propertyValue)
    {
        if (propertyName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase))
        {
            AddFramework(frameworks, propertyValue);
            return;
        }

        if (!propertyName.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var framework in propertyValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddFramework(frameworks, framework);
    }

    private static void AddFramework(List<string> frameworks, string framework)
    {
        framework = framework.Trim();
        if (framework.Length == 0 || frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
            return;

        frameworks.Add(framework);
    }

    private static void CollectCsprojFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir, "*.csproj"))
                result.Add(file);

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectCsprojFiles(subDir, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Cop.Core.ProviderErrors.Report($"Failed to scan projects in '{dir}': {ex.Message}");
        }
    }
}
