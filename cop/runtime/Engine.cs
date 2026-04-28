using System.Diagnostics;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Runs .cop files against a codebase and returns outputs.
/// This is the main entry point for consuming The cop language as a library.
/// </summary>
public static class Engine
{
    // Built-in providers — all accessed uniformly via RegisterSchema + QueryAndRegister
    private static readonly DataProvider[] _rawProviders =
    [
        new FilesystemProvider(),
        new CodeSchemaProvider(),
        new Markdown.MarkdownProvider(),
    ];

    private record BuiltinProvider(DataProvider Instance, ProviderSchema Schema, HashSet<string> CollectionNames);

    private static readonly BuiltinProvider[] _builtinProviders = _rawProviders.Select(ToBuiltin).ToArray();

    private static BuiltinProvider ToBuiltin(DataProvider provider)
    {
        var schema = ProviderSchema.FromJson(provider.GetSchema());
        var collNames = new HashSet<string>(schema.Collections.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return new(provider, schema, collNames);
    }

    /// <summary>
    /// Discovers .cop scripts and source files, then runs all commands.
    /// </summary>
    public static EngineResult Run(string scriptsDir, string rootPath, string? commandName = null, string[]? programArgs = null, string[]? commandFilter = null, Action<string>? diagLog = null)
    {
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        scriptsDir = Path.GetFullPath(scriptsDir);
        rootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(scriptsDir))
            return new EngineResult([], [], [$"Scripts directory not found: {scriptsDir}"]);

        var scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        if (scriptFilePaths.Length == 0)
            return new EngineResult([], [], []);

        var scriptFiles = new List<ScriptFile>();
        var parseErrors = new List<string>();

        foreach (var path in scriptFilePaths)
        {
            try
            {
                var source = File.ReadAllText(path);
                scriptFiles.Add(ScriptParser.Parse(source, path));
            }
            catch (ParseException ex)
            {
                parseErrors.Add(ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                parseErrors.Add($"Error parsing {path}: {ex.Message}");
            }
        }

        if (scriptFiles.Count == 0 && parseErrors.Count > 0)
            return new EngineResult([], [], parseErrors);

        diagLog?.Invoke($"[diag] Script parsing: {phaseSw.ElapsedMilliseconds}ms ({scriptFiles.Count} .cop files)");

        // Validate command name early (before expensive work)
        if (commandName != null)
        {
            var availableCommands = scriptFiles
                .SelectMany(f => f.Commands.Where(c => c.IsCommand).Select(c => c.Name))
                .Distinct()
                .ToList();

            if (!availableCommands.Contains(commandName, StringComparer.OrdinalIgnoreCase))
            {
                var message = availableCommands.Count > 0
                    ? $"Unknown command '{commandName}'. Available commands: {string.Join(", ", availableCommands)}"
                    : $"Unknown command '{commandName}'. No commands are defined in this directory.";
                diagLog?.Invoke($"[diag] Total: {totalSw.ElapsedMilliseconds}ms (aborted: unknown command)");
                return new EngineResult([], parseErrors, [message], commandName);
            }
        }

        // Fatal errors (e.g. failed imports) prevent execution
        var fatalErrors = new List<string>();

        // Create type registry, resolve imports, and detect provider packages
        phaseSw.Restart();
        var providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        var typeRegistry = CreateTypeRegistry(scriptFiles, scriptsDir, parseErrors, fatalErrors, providerPackages: providerPackages);
        diagLog?.Invoke($"[diag] Type registry & imports: {phaseSw.ElapsedMilliseconds}ms");

        if (fatalErrors.Count > 0)
        {
            diagLog?.Invoke($"[diag] Total: {totalSw.ElapsedMilliseconds}ms (aborted: fatal import errors)");
            return new EngineResult([], parseErrors, fatalErrors, commandName);
        }

        // Load external providers (schema registration + data query)
        phaseSw.Restart();
        LoadExternalProviders(typeRegistry, providerPackages, rootPath, parseErrors, fatalErrors, diagLog);
        if (providerPackages.Count > 0)
            diagLog?.Invoke($"[diag] External providers: {phaseSw.ElapsedMilliseconds}ms ({providerPackages.Count} providers)");

        if (fatalErrors.Count > 0)
        {
            diagLog?.Invoke($"[diag] Total: {totalSw.ElapsedMilliseconds}ms (aborted: provider errors)");
            return new EngineResult([], parseErrors, fatalErrors, commandName);
        }

        // Extract query hints from the target command (collection, filters, languages)
        var queryHints = commandName is not null
            ? ExtractCommandQueryHints(scriptFiles, commandName, typeRegistry)
            : null;

        // Query all built-in providers uniformly
        phaseSw.Restart();
        foreach (var bp in _builtinProviders)
        {
            // Build provider-specific query with hints
            IReadOnlyList<string>? reqCols = null;
            FilterExpression? provFilter = null;
            if (queryHints is not null)
                (reqCols, provFilter) = queryHints.ForProvider(bp.CollectionNames);

            // Skip providers whose collections aren't referenced (performance optimization)
            if (queryHints is not null && reqCols is null)
            {
                diagLog?.Invoke($"[diag] {bp.Instance} query: skipped (no collections referenced)");
                continue;
            }

            var query = new ProviderQuery
            {
                RootPath = rootPath,
                ExcludedDirectories = ExcludedDirectoryNames,
                RequestedCollections = reqCols,
                Filter = provFilter
            };

            if (diagLog is not null)
            {
                var parts = new List<string> { $"RootPath={rootPath}" };
                if (reqCols is not null)
                    parts.Add($"Collections=[{string.Join(", ", reqCols)}]");
                if (provFilter is not null)
                    parts.Add($"Filter={FilterExpression.Format(provFilter)}");
                diagLog($"[diag] {bp.Instance} query: {string.Join(", ", parts)}");
            }

            ProviderLoader.QueryAndRegister(bp.Instance, bp.Schema, typeRegistry, query);
            diagLog?.Invoke($"[diag] {bp.Instance} query: {phaseSw.ElapsedMilliseconds}ms");
            phaseSw.Restart();
        }

        // Initialize provider capabilities (document loaders, etc.)
        foreach (var bp in _builtinProviders)
            ProviderLoader.InitializeCapabilities(bp.Instance, typeRegistry, rootPath);

        // Documents are empty — all collections are now global
        List<Document> documents = [];

        var interpreter = new ScriptInterpreter(typeRegistry);
        HashSet<string>? filterSet = commandFilter is { Length: > 0 }
            ? new HashSet<string>(commandFilter, StringComparer.OrdinalIgnoreCase)
            : null;
        phaseSw.Restart();
        var result = interpreter.Run(scriptFiles, documents, commandName, programArgs, filterSet);
        diagLog?.Invoke($"[diag] Interpreter: {phaseSw.ElapsedMilliseconds}ms ({result.Outputs.Count} outputs)");
        diagLog?.Invoke($"[diag] Total: {totalSw.ElapsedMilliseconds}ms");

        return new EngineResult(result.Outputs, parseErrors, [], commandName, result.FileOutputs);
    }

    /// <summary>
    /// Creates and populates a TypeRegistry with type definitions from imports and script files.
    /// </summary>
    private static TypeRegistry CreateTypeRegistry(List<ScriptFile> scriptFiles, string scriptsDir, List<string> errors, List<string> fatalErrors, List<(string Dir, PackageMetadata Meta)>? providerPackages = null)
    {
        var feedPaths = FindFeedPaths(scriptsDir);

        // Add feed paths declared in script files (feed "path")
        foreach (var sf in scriptFiles)
        {
            if (sf.FeedPaths is null) continue;
            var scriptDir = Path.GetDirectoryName(sf.FilePath) ?? scriptsDir;
            foreach (var fp in sf.FeedPaths)
            {
                var resolved = Path.IsPathRooted(fp)
                    ? Path.GetFullPath(fp)
                    : Path.GetFullPath(Path.Combine(scriptDir, fp));
                if (Directory.Exists(resolved) && !feedPaths.Contains(resolved))
                    feedPaths.Add(resolved);
            }
        }

        return CreateTypeRegistry(scriptFiles, feedPaths, errors, fatalErrors, providerPackages: providerPackages);
    }

    /// <summary>
    /// Finds packages/ feed paths by walking up from scriptsDir.
    /// </summary>
    private static List<string> FindFeedPaths(string scriptsDir)
    {
        var paths = new List<string>();
        var dir = scriptsDir;
        while (dir is not null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
                paths.Add(packagesDir);
            dir = Path.GetDirectoryName(dir);
        }
        return paths;
    }

    /// <summary>
    /// Runs packages from feeds: loads packages by name, executes selected rules.
    /// </summary>
    public static EngineResult RunProject(
        List<string> feedPaths,
        List<string> packageNames,
        string rootPath,
        List<string> rules,
        string[]? programArgs = null)
    {
        rootPath = Path.GetFullPath(rootPath);
        var scriptFiles = new List<ScriptFile>();
        var parseErrors = new List<string>();
        var fatalErrors = new List<string>();

        // Load each package's .cop files as full script files (including commands)
        foreach (var packageName in packageNames)
        {
            bool found = false;
            foreach (var feed in feedPaths)
            {
                var feedFull = Path.GetFullPath(feed);
                var packageDir = ImportResolver.FindPackageDir(feedFull, packageName);
                if (packageDir is null) continue;

                string? copDir = null;
                foreach (var subdir in new[] { "src", "types" })
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

                foreach (var file in copFiles)
                {
                    try
                    {
                        var source = File.ReadAllText(file);
                        scriptFiles.Add(ScriptParser.Parse(source, file));
                    }
                    catch (ParseException ex)
                    {
                        parseErrors.Add(ex.Message);
                    }
                }
                found = true;
                break; // found in this feed, stop searching
            }

            if (!found)
                fatalErrors.Add($"Package '{packageName}' not found in any feed");
        }

        if (fatalErrors.Count > 0)
            return new EngineResult([], parseErrors, fatalErrors);

        if (scriptFiles.Count == 0)
            return new EngineResult([], parseErrors, ["No .cop files found in packages"]);

        // Create type registry using feed paths for import resolution
        // Pre-register directly-loaded packages to prevent re-resolution via transitive imports
        var providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        var typeRegistry = CreateTypeRegistry(scriptFiles, feedPaths, parseErrors, fatalErrors, packageNames, providerPackages: providerPackages);

        if (fatalErrors.Count > 0)
            return new EngineResult([], parseErrors, fatalErrors);

        // Load external providers
        LoadExternalProviders(typeRegistry, providerPackages, rootPath, parseErrors, fatalErrors);

        if (fatalErrors.Count > 0)
            return new EngineResult([], parseErrors, fatalErrors);

        // Query all built-in providers uniformly
        foreach (var bp in _builtinProviders)
        {
            ProviderLoader.QueryAndRegister(bp.Instance, bp.Schema, typeRegistry,
                new ProviderQuery { RootPath = rootPath, ExcludedDirectories = ExcludedDirectoryNames });
        }

        // Initialize provider capabilities (document loaders, etc.)
        foreach (var bp in _builtinProviders)
            ProviderLoader.InitializeCapabilities(bp.Instance, typeRegistry, rootPath);

        // Documents are empty — all collections are now global
        List<Document> documents = [];

        // Run each command, or all non-SAVE if no rules specified
        var allOutputs = new List<PrintOutput>();
        var allFileOutputs = new List<FileOutput>();

        var interpreter = new ScriptInterpreter(typeRegistry);

        // Check if any specified rules are let collections (not commands).
        // If so, synthesize RUN CHECK(name) invocations for them.
        var allCommands = scriptFiles.SelectMany(sf => sf.Commands)
            .Where(c => c.IsCommand && c.CommandRef == null)
            .Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allLets = scriptFiles.SelectMany(sf => sf.LetDeclarations)
            .Where(l => (!l.IsValueBinding || l.IsCollectionUnion) && !l.IsRuntime)
            .Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasCheckCommand = allCommands.Contains("CHECK");

        if (rules.Count > 0)
        {
            // Split rules into actual commands vs let collections
            var commandRules = rules.Where(r => allCommands.Contains(r)).ToList();
            var letRules = rules.Where(r => !allCommands.Contains(r) && allLets.Contains(r)).ToList();

            // Run actual commands directly
            foreach (var rule in commandRules)
            {
                var result = interpreter.Run(scriptFiles, documents, rule, programArgs);
                allOutputs.AddRange(result.Outputs);
                if (result.FileOutputs is not null)
                    allFileOutputs.AddRange(result.FileOutputs);
            }

            // For let collections, synthesize RUN CHECK(name) if check command exists
            if (letRules.Count > 0 && hasCheckCommand)
            {
                var runStatements = string.Join("\n", letRules.Select(name => $"RUN CHECK({name})"));
                var wrapperFile = ScriptParser.Parse(runStatements, "<project>");
                scriptFiles.Add(wrapperFile);
                var result = interpreter.Run(scriptFiles, documents, null, programArgs);
                allOutputs.AddRange(result.Outputs);
                if (result.FileOutputs is not null)
                    allFileOutputs.AddRange(result.FileOutputs);
            }

            return new EngineResult(allOutputs, parseErrors, [], rules[0], allFileOutputs);
        }
        else
        {
            // No rules specified: run all non-parameterized commands AND
            // synthesize RUN CHECK(name) for all non-value let collections
            var letNames = scriptFiles.SelectMany(sf => sf.LetDeclarations)
                .Where(l => l.IsExported && (!l.IsValueBinding || l.IsCollectionUnion) && !l.IsRuntime)
                .Select(l => l.Name).ToList();

            if (letNames.Count > 0 && hasCheckCommand)
            {
                var runStatements = string.Join("\n", letNames.Select(name => $"RUN CHECK({name})"));
                var wrapperFile = ScriptParser.Parse(runStatements, "<project>");
                scriptFiles.Add(wrapperFile);
            }

            var result = interpreter.Run(scriptFiles, documents, null, programArgs);
            return new EngineResult(result.Outputs, parseErrors, [], null, result.FileOutputs);
        }
    }

    /// <summary>
    /// Creates a TypeRegistry from script files using the given feed paths for import resolution.
    /// </summary>
    private static TypeRegistry CreateTypeRegistry(List<ScriptFile> scriptFiles, List<string> feedPaths, List<string> errors, List<string> fatalErrors, List<string>? preloadedPackages = null, List<(string Dir, PackageMetadata Meta)>? providerPackages = null)
    {
        var typeRegistry = new TypeRegistry();
        var importResolver = new ImportResolver([.. feedPaths]);

        var resolvedPackages = new HashSet<string>();
        var importedFiles = new List<ScriptFile>();

        // Pre-register packages that were directly loaded (e.g., from RunProject)
        // to prevent re-resolution via transitive imports
        if (preloadedPackages != null)
        {
            foreach (var pkg in preloadedPackages)
                resolvedPackages.Add(pkg);
        }

        // Collect all imports from user script files into a queue
        var importQueue = new Queue<string>();
        foreach (var sf in scriptFiles)
            foreach (var import in sf.Imports)
                importQueue.Enqueue(import);

        // Resolve imports transitively (packages may import other packages)
        while (importQueue.Count > 0)
        {
            var import = importQueue.Dequeue();
            if (!resolvedPackages.Add(import)) continue;

            var packageFile = importResolver.Resolve(import, fatalErrors);
            if (packageFile is null)
            {
                if (!fatalErrors.Any(e => e.Contains(import)))
                    fatalErrors.Add($"Import '{import}' could not be resolved");
                continue;
            }

            var typeErrors = typeRegistry.LoadTypeDefinitions(packageFile.TypeDefinitions);
            errors.AddRange(typeErrors);

            if (packageFile.FlagsDefinitions is not null)
            {
                var flagsErrors = typeRegistry.LoadFlagsDefinitions(packageFile.FlagsDefinitions);
                errors.AddRange(flagsErrors);
            }

            foreach (var coll in packageFile.CollectionDeclarations)
                typeRegistry.RegisterCollection(coll);

            importedFiles.Add(packageFile);

            // Detect provider packages: check for package metadata with provider:clr
            if (providerPackages != null)
                DetectProviderPackage(packageFile.FilePath, import, feedPaths, providerPackages);

            // Enqueue the package's own imports for transitive resolution
            foreach (var subImport in packageFile.Imports)
                importQueue.Enqueue(subImport);
        }

        // Register types from user script files
        foreach (var sf in scriptFiles)
        {
            var localErrors = typeRegistry.LoadTypeDefinitions(sf.TypeDefinitions);
            errors.AddRange(localErrors);

            if (sf.FlagsDefinitions is not null)
            {
                var flagsErrors = typeRegistry.LoadFlagsDefinitions(sf.FlagsDefinitions);
                errors.AddRange(flagsErrors);
            }

            foreach (var coll in sf.CollectionDeclarations)
                typeRegistry.RegisterCollection(coll);
        }

        scriptFiles.AddRange(importedFiles);

        // Register all built-in provider schemas uniformly
        foreach (var bp in _builtinProviders)
            ProviderLoader.RegisterSchema(bp.Instance, typeRegistry);
        typeRegistry.RegisterProgramType();

        return typeRegistry;
    }

    /// <summary>
    /// Directories excluded from both filesystem scanning and source parsing.
    /// These are build artifacts, VCS metadata, and package caches that contain
    /// no user-authored source code worth analyzing.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
        ".nuget", ".dotnet", "packages", "TestResults",
        "__pycache__", ".mypy_cache", ".pytest_cache",
        "dist", "build", "out", ".next", ".cache"
    };

    /// <summary>
    /// Detects if a resolved package is a CLR provider package and adds it to the list.
    /// </summary>
    private static void DetectProviderPackage(string copDirPath, string packageName, List<string> feedPaths, List<(string Dir, PackageMetadata Meta)> providerPackages)
    {
        // copDirPath is the package's src/ or types/ directory.
        // The package root is its parent directory.
        var packageDir = Path.GetDirectoryName(copDirPath);
        if (packageDir is null) return;

        var metadataFile = Path.Combine(packageDir, $"{packageName}.md");
        if (!File.Exists(metadataFile)) return;

        try
        {
            var metadata = PackageMetadata.ParseFromFile(metadataFile);
            if (metadata.IsClrProvider)
                providerPackages.Add((packageDir, metadata));
        }
        catch
        {
            // Metadata parse error — not a provider package
        }
    }

    /// <summary>
    /// Extracted query hints from a target command: per-collection filter expressions
    /// that can be pushed down to providers.
    /// </summary>
    private record QueryHints(Dictionary<string, FilterExpression?> CollectionFilters)
    {
        /// <summary>Gets the filter for a specific collection, or null.</summary>
        public FilterExpression? GetFilter(string collection)
            => CollectionFilters.TryGetValue(collection, out var f) ? f : null;

        /// <summary>Gets all referenced collection names.</summary>
        public IEnumerable<string> Collections => CollectionFilters.Keys;

        /// <summary>
        /// Gets the combined filter and collections for a set of provider-owned collection names.
        /// Returns null filter if none of the provider's collections are referenced.
        /// </summary>
        public (List<string>? RequestedCollections, FilterExpression? Filter) ForProvider(IEnumerable<string> providerCollections)
        {
            var matched = new List<string>();
            var filters = new List<FilterExpression>();
            foreach (var c in providerCollections)
            {
                if (CollectionFilters.TryGetValue(c, out var f))
                {
                    matched.Add(c);
                    if (f is not null) filters.Add(f);
                }
            }
            if (matched.Count == 0) return (null, null);
            var combined = filters.Count switch { 0 => null, 1 => filters[0], _ => new AndFilter(filters) };
            return (matched, combined);
        }
    }

    /// <summary>
    /// Analyzes the target command's collection references and filter chains to extract
    /// pushdown-able query hints. Walks through let bindings recursively to find the
    /// base collection and combines all filters along the chain.
    /// </summary>
    private static QueryHints? ExtractCommandQueryHints(
        List<ScriptFile> scriptFiles, string commandName, TypeRegistry typeRegistry)
    {
        // Find matching command blocks
        var commands = scriptFiles
            .SelectMany(f => f.Commands)
            .Where(c => c.IsCommand && string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (commands.Count == 0) return null;

        // Build let declaration dictionary for resolving transitive references
        var letDeclarations = new Dictionary<string, LetDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (var sf in scriptFiles)
            foreach (var let in sf.LetDeclarations)
                letDeclarations[let.Name] = let;

        // Collect predicate names and definitions for filter inlining
        var predicateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var predicateDefs = new Dictionary<string, List<PredicateDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sf in scriptFiles)
        {
            foreach (var pred in sf.Predicates)
            {
                predicateNames.Add(pred.Name);
                if (!predicateDefs.TryGetValue(pred.Name, out var group))
                {
                    group = [];
                    predicateDefs[pred.Name] = group;
                }
                group.Add(pred);
            }
        }

        var collectionFilters = new Dictionary<string, FilterExpression?>(StringComparer.OrdinalIgnoreCase);

        foreach (var cmd in commands)
        {
            if (cmd.Collection is null) continue;

            // Resolve collection through let bindings to find base collection + accumulated filters
            var allCmdFilters = new List<Expression>(cmd.Filters);
            var baseCollection = ResolveBaseCollection(cmd.Collection, letDeclarations, allCmdFilters);

            // Get the item type for the base collection to extract pushdown hints
            var itemTypeName = typeRegistry.GetCollectionItemType(baseCollection);
            if (itemTypeName is null)
            {
                collectionFilters.TryAdd(baseCollection, null);
                continue;
            }

            var itemTypeDesc = typeRegistry.GetType(itemTypeName);
            var (hints, _) = FilterHintExtractor.Extract(allCmdFilters, itemTypeDesc, predicateNames, predicateDefs);

            // Merge: if same collection from multiple command blocks, AND the filters
            if (collectionFilters.TryGetValue(baseCollection, out var existing) && existing is not null && hints is not null)
                collectionFilters[baseCollection] = FilterExpression.And(existing, hints);
            else
                collectionFilters[baseCollection] = hints ?? existing;
        }

        return collectionFilters.Count > 0 ? new QueryHints(collectionFilters) : null;
    }

    /// <summary>
    /// Walks let bindings to find the base collection name, accumulating filters along the way.
    /// For example: let x = Types:Public → base="Types", filters=[Public].
    /// </summary>
    private static string ResolveBaseCollection(
        string name, Dictionary<string, LetDeclaration> letDeclarations, List<Expression> filters,
        HashSet<string>? visited = null)
    {
        if (!letDeclarations.TryGetValue(name, out var letDecl))
            return name; // Not a let — it's a direct collection reference

        if (letDecl.IsValueBinding)
            return name; // Value binding, not a collection chain

        visited ??= new(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(name))
            return name; // Cycle detected

        // Prepend let filters (they apply before the command's own filters)
        filters.InsertRange(0, letDecl.Filters);

        // Recurse into the let's base collection
        return ResolveBaseCollection(letDecl.BaseCollection, letDeclarations, filters, visited);
    }

    /// <summary>
    /// Loads external CLR providers: registers their schemas into the type registry
    /// and queries them for collection data.
    /// </summary>
    private static void LoadExternalProviders(TypeRegistry typeRegistry, List<(string Dir, PackageMetadata Meta)> providerPackages, string rootPath, List<string> errors, List<string> fatalErrors, Action<string>? diagLog = null)
    {
        foreach (var (dir, meta) in providerPackages)
        {
            var loaded = ProviderLoader.Load(dir, meta, fatalErrors);
            if (loaded is null) continue;

            // Register schema, types, accessors, and bindings
            ProviderLoader.RegisterSchema(loaded.Instance, typeRegistry);

            diagLog?.Invoke($"[diag] {loaded.Instance} query: RootPath={rootPath}, Format={loaded.Instance.SupportedFormats}, Collections=[{string.Join(", ", loaded.Schema.Collections.Select(c => c.Name))}]");

            // Query for data and register global collections
            ProviderLoader.QueryAndRegister(loaded, typeRegistry, rootPath, errors);

            // Initialize capabilities (document loaders, file parsers, etc.)
            ProviderLoader.InitializeCapabilities(loaded.Instance, typeRegistry, rootPath);
        }
    }
}

/// <summary>
/// Result of running the cop engine.
/// </summary>
public record EngineResult(
    List<PrintOutput> Outputs,
    List<string> ParseErrors,
    List<string> Errors,
    string? CommandName = null,
    List<FileOutput>? FileOutputs = null)
{
    public bool HasParseErrors => ParseErrors.Count > 0;
    public bool HasFatalErrors => Errors.Count > 0;
    public bool IsCommandMode => CommandName != null;
}
