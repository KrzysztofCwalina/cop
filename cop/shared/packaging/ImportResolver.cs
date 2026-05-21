using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Resolves import statements to package type files.
/// Looks up packages in the packages/ directory (default feed).
/// </summary>
public class ImportResolver
{
    private readonly string[] _feedPaths;

    public ImportResolver(params string[] feedPaths)
    {
        _feedPaths = feedPaths;
    }

    /// <summary>
    /// Resolves an import name (e.g., "code") to a parsed ScriptFile containing type definitions.
    /// Searches feed paths for packages/{name}/types/*.cop or packages/{name}/src/*.cop files.
    /// Only exported symbols are returned to the importer.
    /// </summary>
    public ScriptFile? Resolve(string packageName, List<string> errors)
    {
        foreach (var feedPath in _feedPaths)
        {
            var packageDir = FindPackageDir(feedPath, packageName);
            if (packageDir is null) continue;

            var copDir = Path.Combine(packageDir, "src");
            if (!Directory.Exists(copDir)) continue;

            var copFiles = Directory.GetFiles(copDir, "*.cop");
            Array.Sort(copFiles, StringComparer.Ordinal); // deterministic order across platforms
            if (copFiles.Length == 0) continue;

            var allTypes = new List<TypeDefinition>();
            var allCollections = new List<CollectionDeclaration>();
            var allLets = new List<LetDeclaration>();
            var allPredicates = new List<PredicateDefinition>();
            var allFunctions = new List<FunctionDefinition>();
            var allCommands = new List<CommandBlock>();
            var allImports = new List<string>();
            var allFlags = new List<FlagsDefinition>();
            var allEnums = new List<EnumDefinition>();
            var allTypeImports = new List<TypeImportDeclaration>();
            bool hasErrors = false;

            foreach (var file in copFiles)
            {
                try
                {
                    var source = File.ReadAllText(file);
                    var parsed = Cop.Lang.Parser.CopParser.ParseFile(source, file);
                    allTypes.AddRange(parsed.TypeDefinitions);
                    allCollections.AddRange(parsed.CollectionDeclarations);
                    allLets.AddRange(parsed.LetDeclarations);
                    allPredicates.AddRange(parsed.Predicates);
                    allFunctions.AddRange(parsed.Functions);
                    allCommands.AddRange(parsed.Commands);
                    allImports.AddRange(parsed.Imports);
                    if (parsed.FlagsDefinitions != null)
                        allFlags.AddRange(parsed.FlagsDefinitions);
                    if (parsed.EnumDefinitions != null)
                        allEnums.AddRange(parsed.EnumDefinitions);
                    if (parsed.TypeImports != null)
                        allTypeImports.AddRange(parsed.TypeImports.Where(ti => ti.IsExported));
                }
                catch (ParseException ex)
                {
                    errors.Add(ex.Message);
                    hasErrors = true;
                }
                catch (IOException ex)
                {
                    errors.Add($"Could not read '{file}': {ex.Message}");
                    hasErrors = true;
                }
            }

            if (hasErrors) return null;

            // Only exported symbols are visible to importers
            var exportedTypes = allTypes.Where(t => t.IsExported).ToList();
            var exportedCollections = allCollections.Where(c => c.IsExported).ToList();
            var exportedPredicates = allPredicates.Where(p => p.IsExported).ToList();
            var exportedFunctions = allFunctions.Where(f => f.IsExported).ToList();
            var exportedCommands = allCommands.Where(c => c.IsExported).ToList();
            var exportedFlags = allFlags.Where(f => f.IsExported).ToList();
            var exportedEnums = allEnums.Where(e => e.IsExported).ToList();

            // Exported lets + transitive non-exported let dependencies
            var exportedLets = IncludeLetDependencies(allLets);

            return new ScriptFile(
                copDir,
                allImports,
                exportedTypes,
                exportedCollections,
                exportedLets,
                exportedPredicates,
                exportedFunctions,
                exportedCommands,
                FlagsDefinitions: exportedFlags.Count > 0 ? exportedFlags : null,
                EnumDefinitions: exportedEnums.Count > 0 ? exportedEnums : null,
                TypeImports: allTypeImports.Count > 0 ? allTypeImports : null);
        }

        return null;
    }

    /// <summary>
    /// Returns exported lets plus any non-exported lets that are transitive dependencies
    /// of exported lets (referenced in their BaseCollection chain).
    /// E.g., `export let Statements = cb.Statements` needs non-exported `let cb = object('csharp')`.
    /// </summary>
    private static List<LetDeclaration> IncludeLetDependencies(List<LetDeclaration> allLets)
    {
        var exported = allLets.Where(l => l.IsExported).ToList();
        var nonExported = allLets.Where(l => !l.IsExported).ToDictionary(l => l.Name);
        var included = new HashSet<string>(exported.Select(l => l.Name));

        foreach (var let in exported)
            IncludeDeps(let, nonExported, included);

        // Add any non-exported lets that were pulled in as dependencies
        var result = new List<LetDeclaration>(exported);
        foreach (var kvp in nonExported)
        {
            if (included.Contains(kvp.Key))
                result.Add(kvp.Value);
        }
        return result;
    }

    private static void IncludeDeps(LetDeclaration let, Dictionary<string, LetDeclaration> nonExported, HashSet<string> included)
    {
        var baseCol = let.BaseCollection;
        if (string.IsNullOrEmpty(baseCol)) return;

        // Extract the name before the first dot (e.g., "cb" from "cb.Statements")
        var dotIdx = baseCol.IndexOf('.');
        var refName = dotIdx > 0 ? baseCol[..dotIdx] : baseCol;

        if (!included.Contains(refName) && nonExported.TryGetValue(refName, out var dep))
        {
            included.Add(refName);
            IncludeDeps(dep, nonExported, included);
        }
    }

    /// <summary>
    /// Finds a package directory by name, searching recursively through group folders.
    /// A directory is a package if it contains cop.json. Non-package directories are group folders.
    /// </summary>
    public static string? FindPackageDir(string feedPath, string packageName)
    {
        if (!Directory.Exists(feedPath)) return null;

        // Direct child match
        var direct = Path.Combine(feedPath, packageName);
        if (Directory.Exists(direct) && IsPackageDir(direct))
            return direct;

        // Recurse into group folders (non-package subdirectories)
        foreach (var subDir in Directory.GetDirectories(feedPath))
        {
            var dirName = Path.GetFileName(subDir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.')) continue;
            if (IsPackageDir(subDir)) continue; // skip actual packages

            var result = FindPackageDir(subDir, packageName);
            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>
    /// Returns true if a directory is a package (contains cop.json).
    /// </summary>
    private static bool IsPackageDir(string dirPath)
    {
        return File.Exists(Path.Combine(dirPath, PackageMetadata.MetadataFileName));
    }
}