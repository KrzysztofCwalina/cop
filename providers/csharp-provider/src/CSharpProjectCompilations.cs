using System.Xml.Linq;
using Cop.Core;
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
        var projects = CSharpProjectDiscovery.Discover(root, excluded)
            .Select(p => Path.GetFullPath(Path.Combine(root, p.Path.Replace('/', Path.DirectorySeparatorChar))))
            .OrderByDescending(p => Path.GetDirectoryName(p)!.Length).ToArray();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var groups = new Dictionary<string, List<SyntaxTree>>(comparer);
        foreach (var tree in syntaxTrees)
        {
            var path = Path.GetFullPath(tree.FilePath);
            var owner = projects.FirstOrDefault(project =>
                path.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, comparison)) ?? "";
            if (!groups.TryGetValue(owner, out var group)) groups[owner] = group = [];
            group.Add(tree);
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
                    var document = XDocument.Load(project);
                    var elements = document.Descendants().ToList();
                    if (elements.Any(e => e.Name.LocalName is "TargetFrameworks" or "Import"
                        || e.Attribute("Condition") is not null
                        || e.Name.LocalName == "Compile"))
                        ProviderWarnings.ReportOnce("csharp", project,
                            $"'{project}' uses conditional, imported, multi-target, or custom Compile settings. " +
                            "C# semantic analysis uses project-directory membership and the first restored target; full MSBuild evaluation is unavailable.");
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
                    string.Join("\n", imports.Distinct().Select(i => $"global using {i};"))));
            var compilation = CSharpCompilation.Create("CopAnalysis_" + compiled.Count, compilationTrees, metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));
            compiled[project] = compilation;
            visiting.Remove(project);
            return compilation;
        }

        var result = new Dictionary<SyntaxTree, CSharpCompilation>();
        foreach (var (project, trees) in groups)
        {
            var compilation = Compile(project);
            foreach (var tree in trees) result[tree] = compilation;
        }
        return result;
    }
}
