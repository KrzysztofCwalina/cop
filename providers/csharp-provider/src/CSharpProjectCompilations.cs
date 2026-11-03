using Cop.Core;
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
        var allTrees = syntaxTrees.ToArray();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var discovered = CSharpProjectDiscovery.Discover(root, excluded, allTrees.Select(tree => tree.FilePath).ToArray());
        var projectFiles = discovered.ToDictionary(
            project => Path.GetFullPath(project.Path.Replace('/', Path.DirectorySeparatorChar), root),
            project => project.SourceFiles is null ? null : new HashSet<string>(project.SourceFiles, comparer), comparer);
        var projects = projectFiles.Keys.OrderByDescending(p => Path.GetDirectoryName(p)!.Length)
            .ThenBy(p => p, comparer).ToArray();
        var groups = new Dictionary<string, List<SyntaxTree>>(comparer);
        var owners = new Dictionary<SyntaxTree, string>();
        foreach (var tree in allTrees)
        {
            var path = Path.GetFullPath(tree.FilePath);
            var memberships = projects.Where(project => projectFiles[project] is { } sources
                ? sources.Contains(path)
                : path.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, comparison)).ToArray();
            owners[tree] = memberships.FirstOrDefault() ?? "";
            foreach (var project in memberships.Length == 0 ? new[] { "" } : memberships)
            {
                if (!groups.TryGetValue(project, out var group)) groups[project] = group = [];
                group.Add(tree);
            }
        }

        var compiled = new Dictionary<string, CSharpCompilation>(comparer);
        var visiting = new HashSet<string>(comparer);
        CSharpCompilation Compile(string project)
        {
            if (compiled.TryGetValue(project, out var cached)) return cached;
            visiting.Add(project);
            var directory = project.Length == 0 ? root : Path.GetDirectoryName(project)!;
            var metadata = references(directory);
            var trees = groups.GetValueOrDefault(project) ?? [];
            var imports = new List<string>();
            if (project.Length > 0)
            {
                try
                {
                    var document = CSharpProjectDiscovery.LoadProjectDocument(project);
                    var elements = document?.Descendants().ToList() ?? [];
                    var unsupportedSettings = CSharpProjectInputs.UnsupportedSettings(document);
                    if (unsupportedSettings.Count > 0)
                        ProviderWarnings.ReportOnce("csharp", project,
                            $"Limited C# analysis for '{project}': found {string.Join(", ", unsupportedSettings)}. " +
                            "Cop cannot fully evaluate these settings. Source-file membership may fall back to project folders, " +
                            "and only the first restored target framework is used. File inclusion or resolved symbols may differ from your actual build. " +
                            "If a check depends on these settings, compare its results with the files and target framework selected by your normal build. " +
                            "This is a Cop limitation, not a project error; installing the .NET SDK or running restore will not remove it.");
                    foreach (var reference in elements.Where(e => e.Name.LocalName == "ProjectReference"))
                    {
                        var include = reference.Attribute("Include")?.Value;
                        if (include is null) continue;
                        var dependency = Path.GetFullPath(Path.Combine(directory,
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
                        imports.AddRange(["System", "System.Collections.Generic", "System.IO", "System.Linq",
                            "System.Net.Http", "System.Threading", "System.Threading.Tasks"]);
                    foreach (var import in elements.Where(e => e.Name.LocalName == "Using"))
                    {
                        var include = import.Attribute("Include")?.Value;
                        if (include is null) continue;
                        var alias = import.Attribute("Alias")?.Value;
                        var isStatic = import.Attribute("Static")?.Value == "true";
                        imports.Add(alias is not null ? $"{alias} = {include}" : isStatic ? $"static {include}" : include);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    ProviderErrors.Report($"Failed to load project '{project}' for C# analysis: {ex.Message}");
                }
            }
            var compilationTrees = trees.AsEnumerable();
            if (imports.Count > 0)
                compilationTrees = compilationTrees.Append(CSharpSyntaxTree.ParseText(
                    string.Join("\n", imports.Distinct().Select(i => $"global using {i};")),
                    CSharpSourceParser.ParseOptions));
            var compilation = CSharpCompilation.Create("CopAnalysis_" + compiled.Count, compilationTrees, metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));
            compiled[project] = compilation;
            visiting.Remove(project);
            return compilation;
        }

        var result = new Dictionary<SyntaxTree, CSharpCompilation>();
        foreach (var tree in allTrees)
            result[tree] = Compile(owners[tree]);
        return result;
    }
}
