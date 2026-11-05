// Creates Roslyn compilations with each project's source and dependency boundaries.

using System.Xml.Linq;
using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cop.Providers;

internal static class CSharpProjectCompilations
{
    internal static Dictionary<SyntaxTree, CSharpCompilation> Create(string root,
        IEnumerable<SyntaxTree> syntaxTrees, IReadOnlySet<string>? excluded,
        Func<string, List<MetadataReference>> references)
    {
        root = Path.GetFullPath(root);
        SyntaxTree[] allTrees = syntaxTrees.ToArray();
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        List<ProjectInfo> discovered = CSharpProjectDiscovery.Discover(root, excluded, allTrees.Select(tree => tree.FilePath).ToArray());
        Dictionary<string, HashSet<string>?> projectFiles = discovered.ToDictionary(
            project => Path.GetFullPath(project.Path.Replace('/', Path.DirectorySeparatorChar), root),
            project => project.SourceFiles is null ? null : new HashSet<string>(project.SourceFiles, comparer), comparer);
        string[] projects = projectFiles.Keys.OrderByDescending(p => Path.GetDirectoryName(p)!.Length)
            .ThenBy(p => p, comparer).ToArray();
        Dictionary<string, List<SyntaxTree>> groups = new Dictionary<string, List<SyntaxTree>>(comparer);
        Dictionary<SyntaxTree, string> owners = new Dictionary<SyntaxTree, string>();
        foreach (SyntaxTree? tree in allTrees)
        {
            string path = Path.GetFullPath(tree.FilePath);
            string[] memberships = projects.Where(project => projectFiles[project] is { } sources
                ? sources.Contains(path)
                : path.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, comparison)).ToArray();
            owners[tree] = memberships.FirstOrDefault() ?? "";
            foreach (string? project in memberships.Length == 0 ? new[] { "" } : memberships)
            {
                if (!groups.TryGetValue(project, out List<SyntaxTree>? group))
                {
                    groups[project] = group = [];
                }

                group.Add(tree);
            }
        }

        Dictionary<string, CSharpCompilation> compiled = new Dictionary<string, CSharpCompilation>(comparer);
        HashSet<string> visiting = new HashSet<string>(comparer);
        CSharpCompilation Compile(string project)
        {
            if (compiled.TryGetValue(project, out CSharpCompilation? cached))
            {
                return cached;
            }

            visiting.Add(project);
            string directory = project.Length == 0 ? root : Path.GetDirectoryName(project)!;
            List<MetadataReference> metadata = references(directory);
            List<SyntaxTree> trees = groups.GetValueOrDefault(project) ?? [];
            List<string> imports = new List<string>();
            if (project.Length > 0)
            {
                try
                {
                    XDocument? document = CSharpProjectDiscovery.LoadProjectDocument(project);
                    List<XElement> elements = document?.Descendants().ToList() ?? [];
                    string targetFramework = elements
                        .FirstOrDefault(element => element.Name.LocalName is
                            "TargetFramework" or "TargetFrameworks")
                        ?.Value.Split(';', StringSplitOptions.TrimEntries)[0] ?? "";
                    HashSet<string?> referenceNames = metadata
                        .Select(reference => Path.GetFileName(reference.Display))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (XElement framework in elements
                        .Where(element => element.Name.LocalName == "FrameworkReference"))
                    {
                        if (framework.Attribute("Include")?.Value is not { } name)
                        {
                            continue;
                        }
                        foreach (MetadataReference reference in
                            CSharpFrameworkReferences.Resolve(name, targetFramework, project))
                        {
                            if (referenceNames.Add(Path.GetFileName(reference.Display)))
                            {
                                metadata.Add(reference);
                            }
                        }
                    }
                    List<string> unsupportedSettings = CSharpProjectInputs.UnsupportedSettings(document);
                    if (unsupportedSettings.Count > 0)
                    {
                        ProviderWarnings.ReportOnce("csharp", project,
                            $"Limited C# analysis for '{project}': found {string.Join(", ", unsupportedSettings)}. " +
                            "Cop cannot fully evaluate these settings. Source-file membership may fall back to project folders, " +
                            "and only the first restored target framework is used. File inclusion or resolved symbols may differ from your actual build. " +
                            "If a check depends on these settings, compare its results with the files and target framework selected by your normal build. " +
                            "This is a Cop limitation, not a project error; installing the .NET SDK or running restore will not remove it.");
                    }

                    foreach (XElement? reference in elements.Where(e => e.Name.LocalName == "ProjectReference"))
                    {
                        string? include = reference.Attribute("Include")?.Value;
                        if (include is null)
                        {
                            continue;
                        }

                        string dependency = Path.GetFullPath(Path.Combine(directory,
                            include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                        if (!projects.Contains(dependency, comparer))
                        {
                            ProviderWarnings.ReportOnce("csharp", dependency,
                                $"Referenced project '{dependency}' is outside the analyzed source set; its source symbols are unavailable.");
                            continue;
                        }
                        if (visiting.Contains(dependency))
                        {
                            ProviderWarnings.ReportOnce("csharp", project, $"Cyclic project reference in '{project}'; semantic resolution is incomplete.");
                            continue;
                        }
                        metadata.Add(Compile(dependency).ToMetadataReference());
                    }
                    if (elements.Any(e => e.Name.LocalName == "ImplicitUsings" && e.Value.Trim() is "enable" or "true"))
                    {
                        imports.AddRange(["System", "System.Collections.Generic", "System.IO", "System.Linq",
                            "System.Net.Http", "System.Threading", "System.Threading.Tasks"]);
                    }

                    foreach (XElement? import in elements.Where(e => e.Name.LocalName == "Using"))
                    {
                        string? include = import.Attribute("Include")?.Value;
                        if (include is null)
                        {
                            continue;
                        }

                        string? alias = import.Attribute("Alias")?.Value;
                        bool isStatic = import.Attribute("Static")?.Value == "true";
                        imports.Add(alias is not null ? $"{alias} = {include}" : isStatic ? $"static {include}" : include);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    ProviderErrors.Report($"Failed to load project '{project}' for C# analysis: {ex.Message}");
                }
            }
            IEnumerable<SyntaxTree> compilationTrees = trees.AsEnumerable();
            if (imports.Count > 0)
            {
                compilationTrees = compilationTrees.Append(CSharpSyntaxTree.ParseText(
                    string.Join("\n", imports.Distinct().Select(i => $"global using {i};")),
                    CSharpSourceParser.ParseOptions));
            }

            CSharpCompilation compilation = CSharpCompilation.Create("CopAnalysis_" + compiled.Count, compilationTrees, metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));
            compiled[project] = compilation;
            visiting.Remove(project);
            return compilation;
        }

        Dictionary<SyntaxTree, CSharpCompilation> result = new Dictionary<SyntaxTree, CSharpCompilation>();
        foreach (SyntaxTree? tree in allTrees)
        {
            result[tree] = Compile(owners[tree]);
        }

        return result;
    }
}
