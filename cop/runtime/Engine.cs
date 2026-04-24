using System.Diagnostics;
using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;




namespace Cop.Providers;

/// <summary>
/// Runs .cop files against a codebase and returns outputs.
/// This is the main entry point for consuming The cop language as a library.
/// </summary>
public static class Engine
{
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
        LoadExternalProviders(typeRegistry, providerPackages, rootPath, parseErrors, fatalErrors);
        if (providerPackages.Count > 0)
            diagLog?.Invoke($"[diag] External providers: {phaseSw.ElapsedMilliseconds}ms ({providerPackages.Count} providers)");

        if (fatalErrors.Count > 0)
        {
            diagLog?.Invoke($"[diag] Total: {totalSw.ElapsedMilliseconds}ms (aborted: provider errors)");
            return new EngineResult([], parseErrors, fatalErrors, commandName);
        }

        // Scan filesystem and register global collections
        phaseSw.Restart();
        FilesystemTypeRegistrar.Scan(typeRegistry, rootPath);
        diagLog?.Invoke($"[diag] Filesystem scan: {phaseSw.ElapsedMilliseconds}ms");

        // Parse source files only if code collections are referenced
        phaseSw.Restart();
        List<Document> documents;
        if (NeedsSourceParsing(scriptFiles))
        {
            var requiredLanguages = DetectRequiredLanguages(scriptFiles);
            documents = ParseSourceFiles(rootPath, requiredLanguages);
            diagLog?.Invoke($"[diag] Source parsing: {phaseSw.ElapsedMilliseconds}ms ({documents.Count} files, languages: {(requiredLanguages != null ? string.Join(",", requiredLanguages) : "all")})");
        }
        else
        {
            documents = [];
            diagLog?.Invoke($"[diag] Source parsing: skipped (no code collections referenced)");
        }

        var interpreter = new ScriptInterpreter(typeRegistry, externalDocumentLoader: CreateDocumentLoader(typeRegistry));
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

        // Scan filesystem from rootPath
        FilesystemTypeRegistrar.Scan(typeRegistry, rootPath);

        // Parse source files only if code collections are referenced
        List<Document> documents;
        if (NeedsSourceParsing(scriptFiles))
        {
            var requiredLanguages = DetectRequiredLanguages(scriptFiles);
            documents = ParseSourceFiles(rootPath, requiredLanguages);
        }
        else
        {
            documents = [];
        }

        // Run each command, or all non-SAVE if no rules specified
        var allOutputs = new List<PrintOutput>();
        var allFileOutputs = new List<FileOutput>();
        var interpreter = new ScriptInterpreter(typeRegistry, externalDocumentLoader: CreateDocumentLoader(typeRegistry));

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

            foreach (var coll in sf.CollectionDeclarations)
                typeRegistry.RegisterCollection(coll);
        }

        scriptFiles.AddRange(importedFiles);

        CodeTypeRegistrar.Register(typeRegistry);
        FilesystemTypeRegistrar.Register(typeRegistry);
        typeRegistry.RegisterProgramType();

        return typeRegistry;
    }

    /// <summary>
    /// Creates a parser registry with all available language parsers.
    /// This is the composition root — individual parsers live in separate provider projects.
    /// </summary>
    private static SourceParserRegistry CreateParserRegistry()
    {
        var registry = new SourceParserRegistry();
        registry.Register(new CSharpSourceParser());
        registry.Register(new TextFileParser());
        registry.Register(new PythonSourceParser());
        registry.Register(new JavaScriptSourceParser());
        return registry;
    }

    /// <summary>
    /// Discovers and parses source files into Documents.
    /// Normalizes paths, pre-stamps StatementInfo.File references.
    /// When requiredLanguages is provided, only files matching those languages are parsed.
    /// Uses recursive directory walk with early pruning of excluded directories.
    /// </summary>
    private static List<Document> ParseSourceFiles(string rootPath, HashSet<string>? requiredLanguages = null)
    {
        var parserRegistry = CreateParserRegistry();
        var filePaths = new List<string>();
        CollectSourceFiles(rootPath, parserRegistry, requiredLanguages, filePaths);

        // Parse files in parallel — Roslyn parsing is CPU-bound and embarrassingly parallel
        var documents = new System.Collections.Concurrent.ConcurrentBag<Document>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                var ext = Path.GetExtension(filePath);
                var parser = parserRegistry.GetParser(ext);
                if (parser == null) return;

                SourceFile? sourceFile;
                try
                {
                    var text = File.ReadAllText(filePath);
                    sourceFile = parser.Parse(filePath, text);
                }
                catch
                {
                    return;
                }

                if (sourceFile == null) return;

                var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                var normalizedFile = sourceFile with { Path = relativePath };

                // Pre-stamp StatementInfo.File references (in-place, no cloning)
                for (int i = 0; i < normalizedFile.Statements.Count; i++)
                {
                    normalizedFile.Statements[i].File = normalizedFile;
                }

                // Pre-stamp TypeDeclaration.File references
                for (int i = 0; i < normalizedFile.Types.Count; i++)
                {
                    normalizedFile.Types[i] = normalizedFile.Types[i] with { File = normalizedFile };
                }

                documents.Add(new Document(relativePath, normalizedFile.Language, normalizedFile));
            });

        // Sort by path for deterministic order across runs
        return documents.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
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
    /// Recursively collects source file paths, pruning excluded directories early
    /// to avoid walking massive subtrees (e.g., node_modules, .git).
    /// </summary>
    private static void CollectSourceFiles(string dir, SourceParserRegistry parserRegistry, HashSet<string>? requiredLanguages, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(file);
                var parser = parserRegistry.GetParser(ext);
                if (parser == null) continue;
                if (requiredLanguages is not null && !requiredLanguages.Contains(parser.Language))
                    continue;
                result.Add(file);
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (ExcludedDirectoryNames.Contains(dirName)) continue;
                CollectSourceFiles(subDir, parserRegistry, requiredLanguages, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Analyzes script files to determine which source languages are actually needed.
    /// Scans filter chains, predicate constraints, and predicate bodies for known language identifiers.
    /// Returns null if all languages are needed (no language filters found).
    /// </summary>
    public static HashSet<string>? DetectRequiredLanguages(List<ScriptFile> scriptFiles)
    {
        // Known language names that map to source parsers
        var knownLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "csharp", "python", "javascript" };

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect all identifiers from filter chains and expressions
        foreach (var sf in scriptFiles)
        {
            // Let declaration filters
            foreach (var let in sf.LetDeclarations)
            {
                foreach (var filter in let.Filters)
                    CollectIdentifiers(filter, identifiers);
            }

            // Command collection filters
            foreach (var cmd in sf.Commands)
            {
                foreach (var filter in cmd.Filters)
                    CollectIdentifiers(filter, identifiers);
            }

            // Predicate constraints and bodies
            foreach (var pred in sf.Predicates)
            {
                if (pred.Constraint is not null)
                    identifiers.Add(pred.Constraint);
                CollectIdentifiers(pred.Body, identifiers);
            }
        }

        // Intersect with known languages
        foreach (var id in identifiers)
        {
            if (knownLanguages.Contains(id))
                found.Add(id);
        }

        // If no language identifiers found, all languages are needed
        // Also always include "text" since text files don't depend on language filters
        return found.Count > 0 ? found : null;
    }

    /// <summary>
    /// Recursively collects all identifier names from an expression tree.
    /// </summary>
    private static void CollectIdentifiers(Expression expr, HashSet<string> identifiers)
    {
        switch (expr)
        {
            case IdentifierExpr id:
                identifiers.Add(id.Name);
                break;
            case PredicateCallExpr pc:
                identifiers.Add(pc.Name);
                CollectIdentifiers(pc.Target, identifiers);
                foreach (var arg in pc.Args) CollectIdentifiers(arg, identifiers);
                break;
            case FunctionCallExpr fc:
                foreach (var arg in fc.Args) CollectIdentifiers(arg, identifiers);
                break;
            case MemberAccessExpr ma:
                CollectIdentifiers(ma.Target, identifiers);
                break;
            case BinaryExpr bin:
                CollectIdentifiers(bin.Left, identifiers);
                CollectIdentifiers(bin.Right, identifiers);
                break;
            case UnaryExpr un:
                CollectIdentifiers(un.Operand, identifiers);
                break;
            case ListLiteralExpr list:
                foreach (var elem in list.Elements) CollectIdentifiers(elem, identifiers);
                break;
            case ObjectLiteralExpr obj:
                foreach (var (_, val) in obj.Fields) CollectIdentifiers(val, identifiers);
                break;
        }
    }

    /// <summary>
    /// Checks whether any script references code-level collections that require source parsing.
    /// Returns false if only filesystem collections (DiskFiles, Folders) are used.
    /// </summary>
    public static bool NeedsSourceParsing(List<ScriptFile> scriptFiles)
    {
        var codeCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Types", "Statements", "Lines", "Files", "Members", "Api" };

        foreach (var sf in scriptFiles)
        {
            // If the script imports the code package, parsing is needed
            foreach (var import in sf.Imports)
            {
                if (import.Equals("code", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check let base collections (commands reference lets transitively)
            foreach (var let in sf.LetDeclarations)
            {
                if (IsCodeCollection(let.BaseCollection, codeCollections))
                    return true;
            }
        }

        return false;

        static bool IsCodeCollection(string name, HashSet<string> codeCollections)
        {
            // Match exact name (e.g., "Types") or dotted suffix (e.g., "Code.Types")
            if (codeCollections.Contains(name)) return true;
            var dotIdx = name.LastIndexOf('.');
            return dotIdx >= 0 && codeCollections.Contains(name[(dotIdx + 1)..]);
        }
    }

    /// <summary>
    /// Creates the external document loader delegate for Load('path').
    /// Returns Documents wrapping SourceFiles — the same model as implicit source loading.
    /// The existing collection extractors (Types, Api, Statements, etc.) handle extraction.
    /// </summary>
    private static Func<string, List<Document>> CreateDocumentLoader(TypeRegistry typeRegistry)
    {
        return (string path) =>
        {
            var sourceFile = AssemblyApiReader.ReadAssembly(path);

            // Stamp TypeDeclaration.File references (same as ParseSourceFiles does for source code)
            for (int i = 0; i < sourceFile.Types.Count; i++)
            {
                sourceFile.Types[i] = sourceFile.Types[i] with { File = sourceFile };
            }

            return [new Document(path, sourceFile.Language, sourceFile)];
        };
    }

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
    /// Loads external CLR providers: registers their schemas into the type registry
    /// and queries them for collection data.
    /// </summary>
    private static void LoadExternalProviders(TypeRegistry typeRegistry, List<(string Dir, PackageMetadata Meta)> providerPackages, string rootPath, List<string> errors, List<string> fatalErrors)
    {
        foreach (var (dir, meta) in providerPackages)
        {
            var loaded = ProviderLoader.Load(dir, meta, fatalErrors);
            if (loaded is null) continue;

            // Register types and collections from the provider schema
            typeRegistry.RegisterProviderSchema(loaded.Schema);

            // For JSON providers, also register ScriptObject-based accessors
            if (!loaded.Instance.SupportedFormats.HasFlag(ProviderFormat.Objects))
                JsonCollectionDeserializer.RegisterScriptObjectAccessors(typeRegistry, loaded.Schema);

            // Query for data and register global collections
            ProviderLoader.QueryAndRegister(loaded, typeRegistry, rootPath, errors);
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
