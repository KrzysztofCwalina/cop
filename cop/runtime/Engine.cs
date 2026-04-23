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
    public static EngineResult Run(string scriptsDir, string codebasePath, string? commandName = null, string[]? programArgs = null, string[]? commandFilter = null)
    {
        scriptsDir = Path.GetFullPath(scriptsDir);
        codebasePath = Path.GetFullPath(codebasePath);

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

        // Fatal errors (e.g. failed imports) prevent execution
        var fatalErrors = new List<string>();

        // Create type registry and resolve imports
        var typeRegistry = CreateTypeRegistry(scriptFiles, scriptsDir, parseErrors, fatalErrors);

        if (fatalErrors.Count > 0)
            return new EngineResult([], parseErrors, fatalErrors, commandName);

        // Scan filesystem and register global collections
        FilesystemTypeRegistrar.Scan(typeRegistry, codebasePath);

        // Parse source files into documents
        var documents = ParseSourceFiles(codebasePath);

        // Validate command name if specified
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
                return new EngineResult([], parseErrors, [message], commandName);
            }
        }

        var interpreter = new ScriptInterpreter(typeRegistry, externalCodeLoader: CreateCodeLoader(typeRegistry));
        HashSet<string>? filterSet = commandFilter is { Length: > 0 }
            ? new HashSet<string>(commandFilter, StringComparer.OrdinalIgnoreCase)
            : null;
        var result = interpreter.Run(scriptFiles, documents, commandName, programArgs, filterSet);

        return new EngineResult(result.Outputs, parseErrors, [], commandName, result.FileOutputs);
    }

    /// <summary>
    /// Creates and populates a TypeRegistry with type definitions from imports and script files.
    /// </summary>
    private static TypeRegistry CreateTypeRegistry(List<ScriptFile> scriptFiles, string scriptsDir, List<string> errors, List<string> fatalErrors)
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

        return CreateTypeRegistry(scriptFiles, feedPaths, errors, fatalErrors);
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
        string codebasePath,
        List<string> rules,
        string[]? programArgs = null)
    {
        codebasePath = Path.GetFullPath(codebasePath);
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
        var typeRegistry = CreateTypeRegistry(scriptFiles, feedPaths, parseErrors, fatalErrors, packageNames);

        if (fatalErrors.Count > 0)
            return new EngineResult([], parseErrors, fatalErrors);

        // Scan filesystem from codebasePath
        FilesystemTypeRegistrar.Scan(typeRegistry, codebasePath);

        // Parse source files into documents
        var documents = ParseSourceFiles(codebasePath);

        // Run each command, or all non-SAVE if no rules specified
        var allOutputs = new List<PrintOutput>();
        var allFileOutputs = new List<FileOutput>();
        var interpreter = new ScriptInterpreter(typeRegistry, externalCodeLoader: CreateCodeLoader(typeRegistry));

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
    private static TypeRegistry CreateTypeRegistry(List<ScriptFile> scriptFiles, List<string> feedPaths, List<string> errors, List<string> fatalErrors, List<string>? preloadedPackages = null)
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
    /// Discovers and parses source files into Documents.
    /// Normalizes paths, pre-stamps StatementInfo.File references.
    /// </summary>
    private static List<Document> ParseSourceFiles(string codebasePath)
    {
        var parserRegistry = SourceParserRegistry.CreateDefault();
        var filePaths = Directory.GetFiles(codebasePath, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var normalized = f.Replace('\\', '/');
                if (normalized.Contains("/bin/") || normalized.Contains("/obj/")) return false;
                var ext = Path.GetExtension(f);
                return parserRegistry.GetParser(ext) != null;
            })
            .ToList();

        var documents = new List<Document>();
        foreach (var filePath in filePaths)
        {
            var ext = Path.GetExtension(filePath);
            var parser = parserRegistry.GetParser(ext);
            if (parser == null) continue;

            SourceFile? sourceFile;
            try
            {
                var text = File.ReadAllText(filePath);
                sourceFile = parser.Parse(filePath, text);
            }
            catch
            {
                continue;
            }

            if (sourceFile == null) continue;

            var relativePath = Path.GetRelativePath(codebasePath, filePath).Replace('\\', '/');
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
        }

        return documents;
    }

    /// <summary>
    /// Creates the external code loader delegate for Code.Load('path').
    /// Returns Documents wrapping SourceFiles — the same model as implicit source loading.
    /// The existing collection extractors (Types, Api, Statements, etc.) handle extraction.
    /// </summary>
    private static Func<string, List<Document>> CreateCodeLoader(TypeRegistry typeRegistry)
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