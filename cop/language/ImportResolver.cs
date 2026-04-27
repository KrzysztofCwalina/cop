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

            string? copDir = null;
            foreach (var subdir in new[] { "types", "src" })
            {
                var candidate = Path.Combine(packageDir, subdir);
                if (Directory.Exists(candidate))
                {
                    copDir = candidate;
                    break;
                }
            }
            if (copDir is null) continue;

            var copFiles = Directory.GetFiles(copDir, "*.cop");
            if (copFiles.Length == 0) continue;

            var allTypes = new List<TypeDefinition>();
            var allCollections = new List<CollectionDeclaration>();
            var allLets = new List<LetDeclaration>();
            var allPredicates = new List<PredicateDefinition>();
            var allFunctions = new List<FunctionDefinition>();
            var allCommands = new List<CommandBlock>();
            var allImports = new List<string>();
            var allFlags = new List<FlagsDefinition>();
            bool hasErrors = false;

            foreach (var file in copFiles)
            {
                try
                {
                    var source = File.ReadAllText(file);
                    var parsed = ScriptParser.Parse(source, file);
                    allTypes.AddRange(parsed.TypeDefinitions);
                    allCollections.AddRange(parsed.CollectionDeclarations);
                    allLets.AddRange(parsed.LetDeclarations);
                    allPredicates.AddRange(parsed.Predicates);
                    allFunctions.AddRange(parsed.Functions);
                    allCommands.AddRange(parsed.Commands);
                    allImports.AddRange(parsed.Imports);
                    if (parsed.FlagsDefinitions != null)
                        allFlags.AddRange(parsed.FlagsDefinitions);
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

            return new ScriptFile(
                copDir,
                allImports,
                allTypes,
                allCollections,
                allLets,
                allPredicates,
                allFunctions,
                allCommands.Where(c => c.IsExported).ToList(),
                FlagsDefinitions: allFlags.Count > 0 ? allFlags : null);
        }

        return null;
    }

    /// <summary>
    /// Finds a package directory by name, searching recursively through group folders.
    /// A directory is a package if it contains {dirName}.md. Non-package directories are group folders.
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
    /// Returns true if a directory is a package (contains {dirName}.md, src/, or types/).
    /// </summary>
    private static bool IsPackageDir(string dirPath)
    {
        var dirName = Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(dirName)) return false;
        if (File.Exists(Path.Combine(dirPath, $"{dirName}.md"))) return true;
        if (Directory.Exists(Path.Combine(dirPath, "src"))) return true;
        if (Directory.Exists(Path.Combine(dirPath, "types"))) return true;
        return false;
    }
}