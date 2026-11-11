// Discovers C# projects and their declared dependencies.

using System.Xml.Linq;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

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
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs,
        IReadOnlyList<string>? sourceFiles = null)
    {
        rootPath = Path.GetFullPath(rootPath);
        List<string> csprojPaths = new List<string>();
        CollectCsprojFiles(rootPath, excludedDirs, csprojPaths);
        if (sourceFiles is null)
        {
            SourceParserRegistry parsers = new SourceParserRegistry();
            parsers.Register(new CSharpSourceParser());
            List<string> paths = new List<string>();
            CodeCollectionBuilder.CollectSourceFiles(rootPath, parsers, excludedDirs, paths);
            sourceFiles = paths;
        }

        // First pass: build a map of normalized path → project name
        Dictionary<string, string> pathToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<(string Name, string RelativePath, List<string> RefPaths, List<string> Packages, List<string> Frameworks, Dictionary<string, string> Properties, List<string>? SourceFiles)> projects = new List<(string Name, string RelativePath, List<string> RefPaths, List<string> Packages, List<string> Frameworks, Dictionary<string, string> Properties, List<string>? SourceFiles)>();

        foreach (string csprojPath in csprojPaths)
        {
            (string? name, List<string>? refPaths, List<string>? packages, List<string>? frameworks, Dictionary<string, string>? properties, List<string>? sources) = ParseCsproj(csprojPath, sourceFiles, rootPath);
            string relativePath = Path.GetRelativePath(rootPath, csprojPath).Replace('\\', '/');
            pathToName[Path.GetFullPath(csprojPath)] = name;
            projects.Add((name, relativePath, refPaths, packages, frameworks, properties, sources));
        }

        // Second pass: resolve ProjectReference paths to project names
        List<ProjectInfo> result = new List<ProjectInfo>();
        foreach ((string? name, string? relativePath, List<string>? refPaths, List<string>? packages, List<string>? frameworks, Dictionary<string, string>? properties, List<string>? sources) in projects)
        {
            List<string> references = new List<string>();
            string csprojDir = Path.GetDirectoryName(Path.Combine(rootPath, NormalizePath(relativePath))) ?? rootPath;

            foreach (string refPath in refPaths)
            {
                string resolvedPath = Path.GetFullPath(Path.Combine(csprojDir, NormalizePath(refPath)));
                if (pathToName.TryGetValue(resolvedPath, out string? targetName))
                {
                    references.Add(targetName);
                }
                else
                {
                    references.Add(Path.GetFileNameWithoutExtension(NormalizePath(refPath)));
                }
            }

            result.Add(new ProjectInfo(name, relativePath, "csharp", references, packages, frameworks)
            {
                Properties = properties,
                SourceFiles = sources
            });
        }

        return result;
    }

    /// <summary>
    /// Property names to extract from PropertyGroup elements.
    /// </summary>
    private static readonly HashSet<string> s_propertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OutputType", "TargetFramework", "TargetFrameworks",
        "IsTestProject", "IsPackable", "RootNamespace",
        "Nullable", "ImplicitUsings", "LangVersion",
    };

    private static (string Name, List<string> RefPaths, List<string> Packages, List<string> Frameworks, Dictionary<string, string> Properties, List<string>? SourceFiles)
        ParseCsproj(string csprojPath, IReadOnlyList<string> sourceFiles, string rootPath)
    {
        string name = Path.GetFileNameWithoutExtension(csprojPath);
        List<string> refPaths = new List<string>();
        List<string> packages = new List<string>();
        List<string> frameworks = new List<string>();
        Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string>? sources = null;

        try
        {
            XDocument? doc = LoadProjectDocument(csprojPath);
            if (doc is null)
            {
                return (name, refPaths, packages, frameworks, properties, sources);
            }

            sources = CSharpProjectInputs.Resolve(csprojPath, doc, sourceFiles, rootPath);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // Extract properties from PropertyGroup elements
            foreach (XElement propGroup in doc.Root?.Elements(ns + "PropertyGroup") ?? [])
            {
                foreach (XElement el in propGroup.Elements())
                {
                    string elName = el.Name.LocalName;
                    string elValue = el.Value;
                    if (s_propertyNames.Contains(elName))
                    {
                        properties[elName] = elValue.Trim();
                        AddTargetFrameworks(frameworks, elName, elValue);
                    }
                }
            }

            // Try to get AssemblyName; fall back to filename
            string? assemblyName = doc.Root?.Descendants(ns + "AssemblyName").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                name = assemblyName;
            }

            // Extract ProjectReference Include paths
            foreach (XElement projRef in doc.Root?.Descendants(ns + "ProjectReference") ?? [])
            {
                string? include = projRef.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    refPaths.Add(include);
                }
            }

            // Extract PackageReference names
            foreach (XElement pkgRef in doc.Root?.Descendants(ns + "PackageReference") ?? [])
            {
                string? include = pkgRef.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    packages.Add(include);
                }
            }

            // Extract FrameworkReference names
            foreach (XElement fwRef in doc.Root?.Descendants(ns + "FrameworkReference") ?? [])
            {
                string? include = fwRef.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    frameworks.Add(include);
                }
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            Cop.Core.ProviderErrors.Report($"Failed to read project '{csprojPath}': {ex.Message}");
        }

        return (name, refPaths, packages, frameworks, properties, sources);
    }

    internal static XDocument? LoadProjectDocument(string path)
    {
        using FileStream stream = File.OpenRead(path);
        try
        {
            return XDocument.Load(stream);
        }
        catch (System.Xml.XmlException)
        {
            stream.Position = 0;
            using StreamReader reader = new StreamReader(stream);
            if (!string.IsNullOrWhiteSpace(reader.ReadToEnd()))
            {
                throw;
            }

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
        {
            return;
        }

        foreach (string framework in propertyValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddFramework(frameworks, framework);
        }
    }

    private static void AddFramework(List<string> frameworks, string framework)
    {
        framework = framework.Trim();
        if (framework.Length == 0 || frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        frameworks.Add(framework);
    }

    private static void CollectCsprojFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir, "*.csproj"))
            {
                result.Add(file);
            }

            foreach (string subDir in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName))
                {
                    continue;
                }

                CollectCsprojFiles(subDir, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Cop.Core.ProviderErrors.Report($"Failed to scan projects in '{dir}': {ex.Message}");
        }
    }
}
