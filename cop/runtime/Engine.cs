using System.Diagnostics;
using Cop.Core;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

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

    private record BuiltinProvider(string Name, DataProvider Instance, ProviderSchema Schema, HashSet<string> CollectionNames);

    public sealed record ParsedModule(string FilePath, string Source, ModuleNode Module);

    private static readonly BuiltinProvider[] _builtinProviders = _rawProviders.Select(ToBuiltin).ToArray();

    private static BuiltinProvider ToBuiltin(DataProvider provider)
    {
        var schema = ProviderSchema.FromJson(provider.GetSchema());
        var collNames = new HashSet<string>(schema.Collections.Select(c => c.Name), StringComparer.Ordinal);
        var name = provider switch
        {
            FilesystemProvider => "filesystem",
            CodeSchemaProvider => "code",
            Markdown.MarkdownProvider => "markdown",
            _ => provider.ToString() ?? provider.GetType().Name
        };
        return new(name, provider, schema, collNames);
    }

    /// <summary>
    /// Discovers .cop scripts and source files, then runs all commands.
    /// </summary>
    public static EngineResult Run(string scriptsDir, string rootPath, string? commandName = null, string[]? programArgs = null, string[]? commandFilter = null, Action<string>? diagLog = null, bool assertMode = false, string[]? additionalFeedPaths = null)
    {
        var totalSw = Stopwatch.StartNew();

        scriptsDir = Path.GetFullPath(scriptsDir);
        rootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(scriptsDir))
            return new EngineResult([], [], [$"Scripts directory not found: {scriptsDir}"]);

        var scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        Array.Sort(scriptFilePaths, StringComparer.Ordinal);
        if (scriptFilePaths.Length == 0)
            return new EngineResult([], [], []);

        var parseErrors = new List<string>();
        var modules = ParseModules(scriptFilePaths, parseErrors);
        if (modules.Count == 0 && parseErrors.Count > 0)
            return new EngineResult([], parseErrors, []);

        var feedPaths = CollectFeedPaths(scriptsDir, modules, additionalFeedPaths);
        var result = ExecuteModules(
            modules,
            feedPaths,
            rootPath,
            parseErrors,
            commandName,
            programArgs,
            commandFilter,
            diagLog,
            topLevelProviderPackages: null);

        diagLog?.Invoke($"[diag] Total: {totalSw.ElapsedMilliseconds}ms");
        return result;
    }

    /// <summary>
    /// Runs a streaming command (e.g., HTTP server) that processes items indefinitely.
    /// Returns only when cancelled via the CancellationToken.
    /// </summary>
    public static async Task RunStreamingAsync(
        string scriptsDir,
        string? commandName,
        CancellationToken cancellationToken,
        Action<string>? diagLog = null,
        string[]? additionalFeedPaths = null)
    {
        await Task.Yield();
        throw new NotImplementedException("Streaming support has not been reimplemented for the new evaluator pipeline yet.");
    }


    private static List<ParsedModule> ParseModules(IEnumerable<string> filePaths, List<string> parseErrors)
    {
        var modules = new List<ParsedModule>();
        foreach (var path in filePaths)
        {
            try
            {
                var source = File.ReadAllText(path);
                modules.Add(new ParsedModule(path, source, CopParser.Parse(source, path)));
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

        return modules;
    }

    private static List<string> CollectFeedPaths(string scriptsDir, IEnumerable<ParsedModule> modules, string[]? additionalFeedPaths)
    {
        var feedPaths = FindFeedPaths(scriptsDir);
        var seen = new HashSet<string>(feedPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            var scriptDir = Path.GetDirectoryName(module.FilePath) ?? scriptsDir;
            foreach (var feedPath in ModuleLoader.ExtractFeedPaths(module.Source, scriptDir))
            {
                if (seen.Add(feedPath))
                    feedPaths.Add(feedPath);
            }
        }

        if (additionalFeedPaths is not null)
        {
            foreach (var feedPath in additionalFeedPaths)
            {
                var resolved = Path.GetFullPath(feedPath);
                if (Directory.Exists(resolved) && seen.Add(resolved))
                    feedPaths.Add(resolved);
            }
        }

        return feedPaths;
    }

    private static EngineResult ExecuteModules(
        List<ParsedModule> modules,
        List<string> feedPaths,
        string rootPath,
        List<string> parseErrors,
        string? commandName,
        string[]? programArgs,
        string[]? commandFilter,
        Action<string>? diagLog,
        List<(string Dir, PackageMetadata Meta)>? topLevelProviderPackages)
    {
        var outputs = new List<PrintOutput>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var asserts = new List<AssertResult>();
        var fileOutputLines = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var bridge = CreateBridge(outputs, fileOutputLines, asserts, diagLog);
        bridge.Evaluator.TraceLog = diagLog;
        RegisterProgram(bridge, programArgs);
        RegisterPlaceholderCollections(bridge.Evaluator.GlobalEnvironment, _builtinProviders.Select(p => (p.Name, p.Schema)));

        // Phase 1: Register functions, types, enums (NOT let bindings — those need provider data)
        foreach (var module in modules)
        {
            try
            {
                bridge.Evaluator.RegisterDeclarations(module.Module);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add(ex.Message);
            }
        }

        // Resolve imports (also phase-1 only: functions/types from imported packages)
        var moduleLoader = new ModuleLoader(feedPaths);
        try
        {
            moduleLoader.ResolveImports(modules.Select(m => m.Module), bridge.Evaluator);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add(ex.Message);
        }

        warnings.AddRange(moduleLoader.Errors);

        var providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        var seenProviderDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (topLevelProviderPackages is not null)
        {
            foreach (var providerPackage in topLevelProviderPackages)
            {
                if (seenProviderDirs.Add(providerPackage.Dir))
                    providerPackages.Add(providerPackage);
            }
        }

        foreach (var (dir, _) in moduleLoader.ProviderPackages)
        {
            diagLog?.Invoke($"[diag] Discovered provider package: {dir}");
            var metadata = PackageMetadata.TryLoadFromDirectory(dir);
            if (metadata is not null && metadata.IsProvider && seenProviderDirs.Add(dir))
                providerPackages.Add((dir, metadata));
        }

        var query = new ProviderQuery
        {
            RootPath = rootPath,
            ExcludedDirectories = ExcludedDirectoryNames
        };

        var queryService = new ProviderQueryService(rootPath, ExcludedDirectoryNames, diagLog);

        foreach (var (dir, meta) in providerPackages)
        {
            var loaded = ProviderLoader.Load(dir, meta, errors, out _, out _);
            diagLog?.Invoke($"[diag] Loading provider from {dir}: {(loaded is null ? "FAILED" : loaded.PackageName)}");
            if (loaded is null)
                continue;

            queryService.RegisterProvider(loaded.PackageName, loaded.Instance, loaded.Schema);
            var collections = QueryProviderCollections(loaded.Instance, loaded.Schema, query, errors);
            diagLog?.Invoke($"[diag] Provider '{loaded.PackageName}' returned {collections.Count} collections: {string.Join(", ", collections.Select(c => $"{c.Key}({c.Value.Count})"))}");
            var runtimeBindings = loaded.Instance.GetRuntimeBindings();
            RegisterProviderCollections(bridge.Evaluator.GlobalEnvironment, loaded.PackageName, collections, loaded.Schema, runtimeBindings);
        }

        foreach (var builtinProvider in _builtinProviders)
        {
            queryService.RegisterProvider(builtinProvider.Name, builtinProvider.Instance, builtinProvider.Schema);
            var collections = QueryProviderCollections(builtinProvider.Instance, builtinProvider.Schema, query, errors);
            diagLog?.Invoke($"[diag] Provider '{builtinProvider.Name}' returned {collections.Count} collections: {string.Join(", ", collections.Select(c => $"{c.Key}({c.Value.Count})"))}");
            var runtimeBindings = builtinProvider.Instance.GetRuntimeBindings();
            RegisterProviderCollections(bridge.Evaluator.GlobalEnvironment, builtinProvider.Name, collections, builtinProvider.Schema, runtimeBindings);
        }

        // Re-register provider intrinsic with query service access (same pattern as print/save/assert)
        bridge.RegisterFunction("provider", (args, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var providerName = args[0].Display();

            if (args.Count > 1 && args[1] is not CopNull)
            {
                var options = args[1].Display();
                var providerQuery = new ProviderQuery
                {
                    RootPath = Path.IsPathRooted(options)
                        ? options
                        : Path.GetFullPath(Path.Combine(rootPath, options)),
                    ExcludedDirectories = ExcludedDirectoryNames
                };
                // Return a queryable collection that defers provider execution until materialization.
                // Filters from predicate chains will be accumulated and pushed to the provider.
                return new CopQueryable(providerName, providerQuery, queryService);
            }

            return new CopProviderProxy(providerName, env);
        });

        // Phase 2: Evaluate let bindings (now that provider data is available)
        // First: deferred let bindings from imported packages
        moduleLoader.EvalDeferredLetBindings(bridge.Evaluator, errors);

        // Then: user module let bindings
        foreach (var module in modules)
        {
            try
            {
                bridge.Evaluator.EvalLetBindings(module.Module);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add(ex.Message);
            }
        }

        // Debug: check what Folders resolves to now
        if (bridge.Evaluator.GlobalEnvironment.TryLookup("Folders", out var foldersVal))
            diagLog?.Invoke($"[diag] After registration, 'Folders' = {foldersVal?.GetType().Name}, items={((foldersVal as CopList)?.Items.Count ?? -1)}");
        else
            diagLog?.Invoke("[diag] After registration, 'Folders' NOT FOUND in env");

        var commandsToRun = commandFilter is { Length: > 0 }
            ? commandFilter.Select(NormalizeCommandName).Distinct(StringComparer.Ordinal).ToList()
            : new List<string> { commandName ?? "main" };

        foreach (var command in commandsToRun)
        {
            try
            {
                diagLog?.Invoke($"[diag] Running command '{command}'");
                var result = bridge.RunCommand(command);
                diagLog?.Invoke($"[diag] Command '{command}' returned: {result?.GetType().Name} = {result?.Display()?.Substring(0, Math.Min(result?.Display()?.Length ?? 0, 100))}");
                // If a command returns a collection (e.g., let-binding used as a named rule),
                // iterate items and produce output for each.
                CollectCollectionOutputs(result, outputs);
                diagLog?.Invoke($"[diag] After collect, outputs count = {outputs.Count}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"Command '{command}' failed: {ex.Message}");
                diagLog?.Invoke($"[diag] Command exception: {ex}");
            }
        }

        errors.AddRange(bridge.Errors);

        var fileOutputs = fileOutputLines
            .Select(kv => new FileOutput(kv.Key, string.Join(System.Environment.NewLine, kv.Value)))
            .ToList();

        string? resultCommandName = commandFilter is { Length: > 0 }
            ? commandsToRun.Count == 1 ? commandsToRun[0] : null
            : commandName ?? "main";

        return new EngineResult(outputs, parseErrors, errors, resultCommandName, fileOutputs, warnings, asserts);
    }

    private static LanguageBridge CreateBridge(
        List<PrintOutput> outputs,
        Dictionary<string, List<string>> fileOutputLines,
        List<AssertResult> asserts,
        Action<string>? diagLog)
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        ffi.Register("print", (args, env) =>
        {
            var text = args.Count > 0 ? args[0].Display() : string.Empty;
            outputs.Add(CreatePrintOutput(text));
            return CopNull.Instance;
        });

        ffi.Register("debug", (args, env) =>
        {
            var text = args.Count > 0 ? args[0].Display() : string.Empty;
            diagLog?.Invoke($"[debug] {text}");
            return CopNull.Instance;
        });

        ffi.Register("save", (args, env) =>
        {
            if (args.Count < 2)
                return CopNull.Instance;

            var path = args[0].Display();
            var content = args[1].Display();
            if (!fileOutputLines.TryGetValue(path, out var lines))
            {
                lines = [];
                fileOutputLines[path] = lines;
            }

            lines.Add(content);
            return CopNull.Instance;
        });

        ffi.Register("assert", (args, env) =>
        {
            var passed = args.Count == 0 || args[0].IsTruthy;
            var description = args.Count > 1 ? args[1].Display() : "assert";
            asserts.Add(new AssertResult(description, passed, passed ? string.Empty : $"assert failed: {description}", 0));
            return CopNull.Instance;
        });

        return new LanguageBridge(ffi);
    }

    private static void RegisterProgram(LanguageBridge bridge, string[]? programArgs)
    {
        var args = (programArgs ?? [])
            .Select(arg => (CopValue)new CopString(arg))
            .ToList();
        var programObj = new CopObject(new Dictionary<string, CopValue>(StringComparer.Ordinal)
        {
            ["Args"] = new CopList(args)
        }) { TypeName = "Program" };

        // Register as both a value (Program) and FFI function (program())
        bridge.RegisterValue("Program", programObj);
        bridge.RegisterFunction("program", (_, _) => programObj, 0);
    }

    private static void RegisterPlaceholderCollections(Cop.Lang.Interpreter.Environment env, IEnumerable<(string Name, ProviderSchema Schema)> providers)
    {
        foreach (var (providerName, schema) in providers)
        {
            foreach (var collection in schema.Collections)
            {
                env.Define($"{providerName}.{collection.Name}", new CopList([]));
                env.Define(collection.Name, new CopList([]));
            }
        }
    }

    private static Dictionary<string, List<object>> QueryProviderCollections(
        DataProvider provider,
        ProviderSchema schema,
        ProviderQuery query,
        List<string> errors)
    {
        try
        {
            return ProviderLoader.QueryCollections(provider, schema, query);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add($"Provider '{provider}' query failed: {ex.Message}");
            return new Dictionary<string, List<object>>(StringComparer.Ordinal);
        }
    }

    private static void RegisterProviderCollections(Cop.Lang.Interpreter.Environment env, string providerName, Dictionary<string, List<object>> collections, ProviderSchema schema, RuntimeBindings? bindings = null)
    {
        // Build a type lookup from the schema for RecordView-based access
        var typeSchemas = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var collectionItemTypes = schema.Collections.ToDictionary(c => c.Name, c => c.ItemType, StringComparer.Ordinal);

        foreach (var (collectionName, items) in collections)
        {
            IDynamicObjectAdapter adapter = DataObjectAdapter.Instance;

            if (items.Count > 0 && collectionItemTypes.TryGetValue(collectionName, out var itemType))
            {
                if (items[0] is RecordView && typeSchemas.TryGetValue(itemType, out var typeSchema))
                {
                    // Binary format: RecordView items with schema-based slot access
                    adapter = new RecordViewAdapter(typeSchema.Properties, typeName: itemType);
                }
                else if (items[0] is not DataObject && bindings?.Accessors is not null &&
                         bindings.Accessors.TryGetValue(itemType, out var accessors))
                {
                    // CLR object collections: use runtime binding accessors with full type info
                    adapter = new ClrObjectAdapter(accessors, typeName: itemType,
                        allAccessors: bindings.Accessors, clrTypeMappings: bindings.ClrTypeMappings);
                }
            }

            var copList = new CopList(items
                .Select(item => (CopValue)new CopDynamicObject(item, adapter))
                .ToList());
            env.Define($"{providerName}.{collectionName}", copList);
            env.Define(collectionName, copList);
        }

        // Register a provider proxy so "filesystem.Folders" member-access syntax works
        env.Define(providerName, new CopProviderProxy(providerName, env));
    }

    private static void CollectCollectionOutputs(CopValue result, List<PrintOutput> outputs)
    {
        try
        {
            // Force thunks before inspecting the result
            if (result is CopThunk thunk)
                result = thunk.Force();

            IEnumerable<CopValue>? items = result switch
            {
                CopList list when list.Items.Count > 0 => list.Items,
                CopLazyCollection lazy => lazy.Enumerate(),
                _ => null
            };

            if (items is null) return;

            foreach (var item in items)
            {
                var text = item.Display();
                if (!string.IsNullOrEmpty(text))
                    outputs.Add(CreatePrintOutput(text));
            }
        }
        catch (Exception ex)
        {
            outputs.Add(new PrintOutput($"[ERROR in CollectCollectionOutputs] {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static string NormalizeCommandName(string name)
        => string.IsNullOrWhiteSpace(name) ? name : name.ToUpperInvariant();

    private static PrintOutput CreatePrintOutput(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{') || !text.Contains('@') || !text.Contains('}'))
            return new PrintOutput(text);

        var spans = new List<TextSpan>();
        int position = 0;
        while (position < text.Length)
        {
            int open = text.IndexOf('{', position);
            if (open < 0)
            {
                if (position < text.Length)
                    spans.Add(new TextSpan(text[position..]));
                break;
            }

            if (open > position)
                spans.Add(new TextSpan(text[position..open]));

            int at = text.IndexOf('@', open + 1);
            int close = text.IndexOf('}', open + 1);
            if (at < 0 || close < 0 || at > close)
            {
                spans.Add(new TextSpan(text[open].ToString()));
                position = open + 1;
                continue;
            }

            var content = text[(open + 1)..at];
            var annotation = text[(at + 1)..close];
            spans.Add(new TextSpan(content, RichString.ParseAnnotation(annotation)));
            position = close + 1;
        }

        return spans.Any(span => span.HasAnnotations)
            ? new PrintOutput(new RichString(spans))
            : new PrintOutput(text);
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
        string[]? programArgs = null,
        Action<string>? diagLog = null)
    {
        rootPath = Path.GetFullPath(rootPath);

        var parseErrors = new List<string>();
        var fatalErrors = new List<string>();
        var modules = new List<ParsedModule>();
        var providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        var normalizedFeedPaths = feedPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var packageName in packageNames)
        {
            bool found = false;
            foreach (var feedPath in normalizedFeedPaths)
            {
                var packageDir = ImportResolver.FindPackageDir(feedPath, packageName);
                if (packageDir is null)
                    continue;

                var srcDir = Path.Combine(packageDir, "src");
                if (Directory.Exists(srcDir))
                {
                    var copFiles = Directory.GetFiles(srcDir, "*.cop");
                    Array.Sort(copFiles, StringComparer.Ordinal);
                    modules.AddRange(ParseModules(copFiles, parseErrors));
                }

                DetectProviderPackage(Path.Combine(packageDir, "src"), packageName, normalizedFeedPaths, providerPackages, parseErrors);
                found = true;
                break;
            }

            if (!found)
                fatalErrors.Add($"Package '{packageName}' not found in any feed");
        }

        if (fatalErrors.Count > 0)
            return new EngineResult([], parseErrors, fatalErrors);

        if (modules.Count == 0)
            return new EngineResult([], parseErrors, ["No .cop files found in packages"]);

        return ExecuteModules(
            modules,
            normalizedFeedPaths,
            rootPath,
            parseErrors,
            rules.Count == 0 ? "main" : null,
            programArgs,
            rules.Count > 0 ? [.. rules] : null,
            diagLog: diagLog,
            topLevelProviderPackages: providerPackages);
    }

    /// <summary>
    /// Directories excluded from both filesystem scanning and source parsing.
    /// These are build artifacts, VCS metadata, and package caches that contain
    /// no user-authored source code worth analyzing.
    /// </summary>
    internal static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
        ".nuget", ".dotnet", "packages", "TestResults",
        "__pycache__", ".mypy_cache", ".pytest_cache",
        "dist", "build", "out", ".next", ".cache"
    };

    private static void DetectProviderPackage(string copDirPath, string packageName, List<string> feedPaths, List<(string Dir, PackageMetadata Meta)> providerPackages, List<string> errors)
    {
        // copDirPath is the package's src/ or types/ directory (from ImportResolver).
        // The package root is its parent directory.
        var packageDir = Path.GetDirectoryName(copDirPath);
        if (packageDir is null) return;

        var metadata = PackageMetadata.TryLoadFromDirectory(packageDir);
        if (metadata is null) return;

        try
        {
            if (metadata.IsProvider)
                providerPackages.Add((packageDir, metadata));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add($"Failed to parse metadata for package '{packageName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Loads external CLR providers: registers their schemas into the type registry
    /// and queries them for collection data.
    /// </summary>
    private static void LoadExternalProviders(TypeRegistry typeRegistry, List<(string Dir, PackageMetadata Meta)> providerPackages, string rootPath, List<string> errors, List<string> fatalErrors, IReadOnlySet<string>? excludedDirectories = null, ProviderQueryService? queryService = null, Action<string>? diagLog = null)
    {
        foreach (var (dir, meta) in providerPackages)
        {
            var loaded = ProviderLoader.Load(dir, meta, fatalErrors, out var sourceProviders, out var sinkProviders);

            // Register source providers
            foreach (var sp in sourceProviders)
            {
                var spSchema = ProviderLoader.RegisterSchema(sp.Instance, typeRegistry);
                ProviderLoader.RegisterStreamProvider(sp.Instance, spSchema, sp.PackageName, typeRegistry);
            }

            // Register sink providers
            foreach (var sk in sinkProviders)
                ProviderLoader.RegisterSinkProvider(sk.Instance, sk.PackageName, typeRegistry);

            if (loaded is null) continue;

            // Register schema, types, accessors, and bindings
            var schema = ProviderLoader.RegisterSchema(loaded.Instance, typeRegistry);

            // Query for data and register global collections
            ProviderLoader.QueryAndRegister(loaded, typeRegistry, rootPath, errors, excludedDirectories);

            // Initialize capabilities (document loaders, file parsers, etc.)
            ProviderLoader.InitializeCapabilities(loaded.Instance, typeRegistry, rootPath);

            // Register with query service for path-scoped queries
            queryService?.RegisterProvider(loaded.PackageName, loaded.Instance, schema);
        }
    }

    /// <summary>
    /// Phase 1: Loads external provider assemblies and registers their schemas (types, accessors, bindings)
    /// into the type registry WITHOUT querying for data. Returns loaded providers for deferred querying.
    /// </summary>
    private static List<(ProviderLoader.LoadedProvider Loaded, ProviderSchema Schema)> RegisterExternalProviderSchemas(
        TypeRegistry typeRegistry, List<(string Dir, PackageMetadata Meta)> providerPackages,
        string rootPath, List<string> fatalErrors, ProviderQueryService? queryService = null)
    {
        var result = new List<(ProviderLoader.LoadedProvider, ProviderSchema)>();
        foreach (var (dir, meta) in providerPackages)
        {
            var loaded = ProviderLoader.Load(dir, meta, fatalErrors, out var sourceProviders, out var sinkProviders);

            // Register source providers
            foreach (var sp in sourceProviders)
            {
                var spSchema = ProviderLoader.RegisterSchema(sp.Instance, typeRegistry);
                ProviderLoader.RegisterStreamProvider(sp.Instance, spSchema, sp.PackageName, typeRegistry);
            }

            // Register sink providers
            foreach (var sk in sinkProviders)
                ProviderLoader.RegisterSinkProvider(sk.Instance, sk.PackageName, typeRegistry);

            if (loaded is null) continue;

            // Register schema, types, accessors, and bindings (no data query)
            var schema = ProviderLoader.RegisterSchema(loaded.Instance, typeRegistry);

            // Initialize capabilities (document loaders, file parsers, etc.)
            ProviderLoader.InitializeCapabilities(loaded.Instance, typeRegistry, rootPath);

            // Register with query service for path-scoped queries
            queryService?.RegisterProvider(loaded.PackageName, loaded.Instance, schema);

            result.Add((loaded, schema));
        }
        return result;
    }

    /// <summary>
    /// Prepares a REPL session: parses .cop files, resolves imports, builds type registry,
    /// but does NOT query providers (lazy loading). Returns a ReplContext for interactive use.
    /// </summary>
    public static ReplContext? PrepareRepl(string scriptsDir, string rootPath, List<string> errors)
    {
        scriptsDir = Path.GetFullPath(scriptsDir);
        rootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(scriptsDir))
        {
            errors.Add($"Scripts directory not found: {scriptsDir}");
            return null;
        }

        // REPL only loads top-level .cop files; packages are resolved via import directives
        var scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
        Array.Sort(scriptFilePaths, StringComparer.Ordinal);
        var modules = new List<ParsedModule>();
        var parseErrors = new List<string>();

        foreach (var path in scriptFilePaths)
        {
            try
            {
                var source = File.ReadAllText(path);
                modules.Add(new ParsedModule(path, source, Cop.Lang.Parser.CopParser.Parse(source, path)));
            }
            catch (ParseException ex)
            {
                if (!IsValueOnlyFile(path))
                    parseErrors.Add(ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!IsValueOnlyFile(path))
                    parseErrors.Add($"Error parsing {path}: {ex.Message}");
            }
        }

        errors.AddRange(parseErrors);

        // Build type registry from modules using the new pipeline
        var typeRegistry = new TypeRegistry();
        foreach (var bp in _builtinProviders)
            ProviderLoader.RegisterSchema(bp.Instance, typeRegistry);
        typeRegistry.RegisterProgramType();

        // Resolve imports and register types
        var feedPaths = FindFeedPaths(scriptsDir);
        var globalCachePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".cop", "packages");
        if (Directory.Exists(globalCachePath) && !feedPaths.Contains(globalCachePath))
            feedPaths.Add(globalCachePath);

        var moduleLoader = new Cop.Lang.Interpreter.ModuleLoader(feedPaths);
        var bridge = CreateBridge([], [], [], null);
        foreach (var module in modules)
        {
            try { bridge.Evaluator.RegisterDeclarations(module.Module); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex.Message); }
        }
        try { moduleLoader.ResolveImports(modules.Select(m => m.Module), bridge.Evaluator); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(ex.Message); }
        errors.AddRange(moduleLoader.Errors);

        // Detect provider packages
        var providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        foreach (var (dir, _) in moduleLoader.ProviderPackages)
        {
            var metadata = PackageMetadata.TryLoadFromDirectory(dir);
            if (metadata is not null && metadata.IsProvider)
                providerPackages.Add((dir, metadata));
        }

        // Load type definitions into TypeRegistry for completion
        foreach (var module in modules)
        {
            foreach (var decl in module.Module.Declarations)
            {
                if (decl is Cop.Lang.Ast.TypeDecl typeDecl)
                {
                    var props = typeDecl.Properties.Select(p => new PropertyDefinition(p.Name, p.Type.Name, p.IsOptional, p.Type.IsCollection, p.Line)).ToList();
                    typeRegistry.LoadTypeDefinitions([new TypeDefinition(typeDecl.Name, typeDecl.BaseType, props, 0, typeDecl.IsExported, typeDecl.DocComment)]);
                }
            }
        }

        return new ReplContext(modules, typeRegistry, rootPath, scriptsDir, providerPackages,
            totalFileCount: scriptFilePaths.Length);
    }

    /// <summary>
    /// Returns true if a .cop file contains only value expressions (list literals, strings, numbers)
    /// that are valid REPL content but not valid top-level cop statements.
    /// </summary>
    private static bool IsValueOnlyFile(string path)
    {
        try
        {
            var firstLine = File.ReadLines(path).FirstOrDefault()?.TrimStart();
            if (firstLine is null) return false;
            return firstLine.StartsWith('[') || firstLine.StartsWith('\'') ||
                   (firstLine.Length > 0 && (char.IsDigit(firstLine[0]) ||
                    (firstLine[0] == '-' && firstLine.Length > 1 && char.IsDigit(firstLine[1]))));
        }
        catch { return false; }
    }

    /// <summary>
    /// Loads provider data into the REPL context (lazy loading on first reference).
    /// </summary>
    public static void LoadProviders(ReplContext context)
    {
        if (context.ProvidersLoaded) return;

        var errors = new List<string>();
        var fatalErrors = new List<string>();

        // Load external providers
        LoadExternalProviders(context.TypeRegistry, context.ProviderPackages, context.RootPath, errors, fatalErrors, ExcludedDirectoryNames, context.QueryService);

        // Query all built-in providers
        foreach (var bp in _builtinProviders)
        {
            var query = new ProviderQuery
            {
                RootPath = context.RootPath,
                ExcludedDirectories = ExcludedDirectoryNames
            };
            ProviderLoader.QueryAndRegister(bp.Instance, bp.Schema, bp.Name, context.TypeRegistry, query);
        }

        // Initialize capabilities
        foreach (var bp in _builtinProviders)
            ProviderLoader.InitializeCapabilities(bp.Instance, context.TypeRegistry, context.RootPath);

        // Register built-in providers with query service for path-scoped queries
        foreach (var bp in _builtinProviders)
            context.QueryService.RegisterProvider(bp.Name, bp.Instance, bp.Schema);

        context.ProvidersLoaded = true;

        if (errors.Count > 0)
            context.Warnings.AddRange(errors);
    }
}

/// <summary>
/// Context object for a REPL session — holds parsed state and supports lazy provider loading.
/// </summary>
public class ReplContext
{
    public List<Engine.ParsedModule> Modules { get; }
    public TypeRegistry TypeRegistry { get; }
    public string RootPath { get; }
    public string ScriptsDir { get; }
    public List<(string Dir, PackageMetadata Meta)> ProviderPackages { get; }
    public bool ProvidersLoaded { get; set; }
    public List<string> Warnings { get; } = [];
    public int TotalFileCount { get; }
    public ProviderQueryService QueryService { get; set; }

    public ReplContext(List<Engine.ParsedModule> modules, TypeRegistry typeRegistry, string rootPath, string scriptsDir, List<(string Dir, PackageMetadata Meta)> providerPackages, int totalFileCount = 0)
    {
        Modules = modules;
        TypeRegistry = typeRegistry;
        RootPath = rootPath;
        ScriptsDir = scriptsDir;
        ProviderPackages = providerPackages;
        TotalFileCount = totalFileCount > 0 ? totalFileCount : modules.Count;
        QueryService = new ProviderQueryService(Directory.GetCurrentDirectory(), Engine.ExcludedDirectoryNames);
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
    List<FileOutput>? FileOutputs = null,
    List<string>? Warnings = null,
    List<AssertResult>? Asserts = null)
{
    public bool HasParseErrors => ParseErrors.Count > 0;
    public bool HasFatalErrors => Errors.Count > 0;
    public bool IsCommandMode => CommandName != null;
}
