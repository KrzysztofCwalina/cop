// Resolves source imports to package locations for program loading.

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
        List<string> incompatibleProviders = new List<string>();
        foreach (string feedPath in _feedPaths)
        {
            string? packageDir = FindPackageDir(feedPath, packageName, incompatibleProviders.Add);
            if (packageDir is null)
            {
                continue;
            }

            string copDir = Path.Combine(packageDir, "src");
            if (!Directory.Exists(copDir))
            {
                continue;
            }

            string[] copFiles = Directory.GetFiles(copDir, "*.cop");
            Array.Sort(copFiles, StringComparer.Ordinal); // deterministic order across platforms
            if (copFiles.Length == 0)
            {
                continue;
            }

            List<TypeDefinition> allTypes = new List<TypeDefinition>();
            List<CollectionDeclaration> allCollections = new List<CollectionDeclaration>();
            List<LetDeclaration> allLets = new List<LetDeclaration>();
            List<PredicateDefinition> allPredicates = new List<PredicateDefinition>();
            List<FunctionDefinition> allFunctions = new List<FunctionDefinition>();
            List<CommandBlock> allCommands = new List<CommandBlock>();
            List<string> allImports = new List<string>();
            List<FlagsDefinition> allFlags = new List<FlagsDefinition>();
            List<EnumDefinition> allEnums = new List<EnumDefinition>();
            List<TypeImportDeclaration> allTypeImports = new List<TypeImportDeclaration>();
            bool hasErrors = false;

            foreach (string file in copFiles)
            {
                try
                {
                    string source = File.ReadAllText(file);
                    ScriptFile parsed = Cop.Lang.Parser.CopParser.ParseFile(source, file);
                    allTypes.AddRange(parsed.TypeDefinitions);
                    allCollections.AddRange(parsed.CollectionDeclarations);
                    allLets.AddRange(parsed.LetDeclarations);
                    allPredicates.AddRange(parsed.Predicates);
                    allFunctions.AddRange(parsed.Functions);
                    allCommands.AddRange(parsed.Commands);
                    allImports.AddRange(parsed.Imports);
                    if (parsed.FlagsDefinitions != null)
                    {
                        allFlags.AddRange(parsed.FlagsDefinitions);
                    }

                    if (parsed.EnumDefinitions != null)
                    {
                        allEnums.AddRange(parsed.EnumDefinitions);
                    }

                    if (parsed.TypeImports != null)
                    {
                        allTypeImports.AddRange(parsed.TypeImports.Where(ti => ti.IsExported));
                    }
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

            if (hasErrors)
            {
                return null;
            }

            // Only exported symbols are visible to importers
            List<TypeDefinition> exportedTypes = allTypes.Where(t => t.IsExported).ToList();
            List<CollectionDeclaration> exportedCollections = allCollections.Where(c => c.IsExported).ToList();
            List<PredicateDefinition> exportedPredicates = allPredicates.Where(p => p.IsExported).ToList();
            List<FunctionDefinition> exportedFunctions = allFunctions.Where(f => f.IsExported).ToList();
            List<CommandBlock> exportedCommands = allCommands.Where(c => c.IsExported).ToList();
            List<FlagsDefinition> exportedFlags = allFlags.Where(f => f.IsExported).ToList();
            List<EnumDefinition> exportedEnums = allEnums.Where(e => e.IsExported).ToList();

            // Exported lets + transitive non-exported let dependencies
            List<LetDeclaration> exportedLets = IncludeLetDependencies(allLets);

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

        errors.AddRange(incompatibleProviders.Distinct(StringComparer.Ordinal));
        return null;
    }

    /// <summary>
    /// Returns exported lets plus any non-exported lets that are transitive dependencies
    /// of exported lets (referenced in their BaseCollection chain).
    /// E.g., `export let Statements = cb.Statements` needs non-exported `let cb = object('csharp')`.
    /// </summary>
    private static List<LetDeclaration> IncludeLetDependencies(List<LetDeclaration> allLets)
    {
        List<LetDeclaration> exported = allLets.Where(l => l.IsExported).ToList();
        Dictionary<string, LetDeclaration> nonExported = allLets.Where(l => !l.IsExported).ToDictionary(l => l.Name);
        HashSet<string> included = new HashSet<string>(exported.Select(l => l.Name));

        foreach (LetDeclaration? let in exported)
        {
            IncludeDeps(let, nonExported, included);
        }

        // Add any non-exported lets that were pulled in as dependencies
        List<LetDeclaration> result = new List<LetDeclaration>(exported);
        foreach (KeyValuePair<string, LetDeclaration> kvp in nonExported)
        {
            if (included.Contains(kvp.Key))
            {
                result.Add(kvp.Value);
            }
        }
        return result;
    }

    private static void IncludeDeps(LetDeclaration let, Dictionary<string, LetDeclaration> nonExported, HashSet<string> included)
    {
        string baseCol = let.BaseCollection;
        if (string.IsNullOrEmpty(baseCol))
        {
            return;
        }

        // Extract the name before the first dot (e.g., "cb" from "cb.Statements")
        int dotIdx = baseCol.IndexOf('.');
        string refName = dotIdx > 0 ? baseCol[..dotIdx] : baseCol;

        if (!included.Contains(refName) && nonExported.TryGetValue(refName, out LetDeclaration? dep))
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
        => FindPackageDir(feedPath, packageName, null);

    public static string? FindPackageDir(string feedPath, string packageName, Action<string>? incompatibleProvider)
    {
        if (!Directory.Exists(feedPath))
        {
            return null;
        }

        Stack<(string Path, int Depth)> pending = new Stack<(string Path, int Depth)>();
        pending.Push((feedPath, 0));
        int visited = 0;
        while (pending.TryPop(out (string Path, int Depth) folder))
        {
            if (++visited > 4096 || folder.Depth > 64)
            {
                throw new IOException("Local package discovery exceeded its 4096-directory / 64-level safety limit.");
            }

            string direct = Path.Combine(folder.Path, packageName);
            if (Directory.Exists(direct)
                && (File.GetAttributes(direct) & FileAttributes.ReparsePoint) == 0 && IsPackageDir(direct))
            {
                PackageMetadata? metadata = PackageMetadata.TryLoadFromDirectory(direct);
                if (metadata is not null && ProviderCompatibility.GetError(direct, metadata) is { } error)
                {
                    incompatibleProvider?.Invoke(error);
                }
                else
                {
                    return direct;
                }
            }
            string[] directories = Directory.EnumerateDirectories(folder.Path).Take(4097).ToArray();
            if (directories.Length > 4096)
            {
                throw new IOException("Local package discovery exceeded its 4096-directory / 64-level safety limit.");
            }

            for (int i = directories.Length - 1; i >= 0; i--)
            {
                string subDir = directories[i];
                string dirName = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.'))
                {
                    continue;
                }

                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0 || IsPackageDir(subDir))
                {
                    continue;
                }

                if (pending.Count + visited >= 4096)
                {
                    throw new IOException("Local package discovery exceeded its 4096-directory / 64-level safety limit.");
                }

                pending.Push((subDir, folder.Depth + 1));
            }
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