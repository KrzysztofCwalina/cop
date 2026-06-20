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

    /// <summary>
    /// When true, the engine times each top-level rule (let-binding) after provider
    /// data is loaded and prints a <c>[profile]</c> breakdown to stderr before running
    /// commands. Enabled via the CLI <c>-rp</c> flag.
    /// </summary>
    public static bool ProfileRules;

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
    public static EngineResult Run(string scriptsDir, string rootPath, string? commandName = null, string[]? programArgs = null, string[]? commandFilter = null, Action<string>? diagLog = null, bool assertMode = false, string[]? additionalFeedPaths = null, string[]? scriptFiles = null, List<(string Dir, PackageMetadata Meta)>? providerPackages = null)
    {
        var totalSw = Stopwatch.StartNew();

        scriptsDir = Path.GetFullPath(scriptsDir);
        rootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(scriptsDir))
            return new EngineResult([], [], [$"Scripts directory not found: {scriptsDir}"]);

        var scriptFilePaths = scriptFiles ?? Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        // Only sort when discovering files ourselves. When scriptFiles is explicitly provided,
        // respect the caller's order (e.g., target file placed last for command priority).
        if (scriptFiles is null)
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
            topLevelProviderPackages: providerPackages,
            assertMode: assertMode);

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

    /// <summary>
    /// Parses modules and also collects structured diagnostics from parse failures.
    /// </summary>
    internal static List<ParsedModule> ParseModulesWithDiagnostics(IEnumerable<string> filePaths, List<string> parseErrors, List<CopDiagnostic> diagnostics)
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
                diagnostics.Add(ex.ToDiagnostic());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                parseErrors.Add($"Error parsing {path}: {ex.Message}");
                diagnostics.Add(new CopDiagnostic(
                    CopDiagnosticSeverity.Error,
                    ex.Message,
                    path));
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
        List<(string Dir, PackageMetadata Meta)>? topLevelProviderPackages,
        bool assertMode = false,
        Dictionary<string, List<ParsedModule>>? packageModuleMap = null)
    {
        var outputs = new List<PrintOutput>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var asserts = new List<AssertResult>();
        var fileOutputLines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var setupSw = Stopwatch.StartNew();

        var bridge = CreateBridge(outputs, fileOutputLines, asserts, diagLog);
        // Per-item evaluator trace ([trace] lines) is a deep-debug firehose (millions of lines on
        // large repos). Enabled only at -ddd (CopDiagnostics.Trace); -d/-dd stay focused on [diag].
        bridge.Evaluator.TraceLog =
            (diagLog is not null && Cop.Core.CopDiagnostics.Trace) ? diagLog : null;

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

        // Validate user modules (same checks as 'cop verify') before execution.
        // This catches issues like enum-vs-string comparisons that would silently fail at runtime.
        // Collect external symbols excluding user-declared names (those are already in the modules being bound).
        var userDeclaredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
            foreach (var decl in module.Module.Declarations)
            {
                var declName = decl switch
                {
                    TypeDecl td => td.Name,
                    EnumDecl ed => ed.Name,
                    FlagsDecl fd => fd.Name,
                    FunctionDecl fd => fd.Name,
                    LetDecl ld => ld.Name,
                    CommandDecl cd => cd.Name,
                    _ => null
                };
                if (declName != null) userDeclaredNames.Add(declName);
            }
        var externalSymbols = Cop.Cli.Commands.VerifyCommand.CollectExternalSymbols(bridge.Evaluator.GlobalEnvironment, moduleLoader.LoadedModules, _builtinProviders.Select(p => p.Schema));
        externalSymbols.RemoveAll(s => userDeclaredNames.Contains(s.Name));
        foreach (var module in modules)
        {
            var binder = new Binder(module.FilePath, externalSymbols);
            var bindingResult = binder.Bind(module.Module);
            foreach (var diag in bindingResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                    errors.Add($"{diag.FilePath ?? module.FilePath}({diag.Line}): error: {diag.Message}");
            }
        }
        if (errors.Count > 0)
            return new EngineResult(outputs, parseErrors, errors);

        // Build TypeRegistry for trait dispatch and computed property resolution
        var typeRegistry = new TypeRegistry();
        foreach (var bp in _builtinProviders)
            ProviderLoader.RegisterSchema(bp.Instance, typeRegistry);
        // Only load types that define traits or conformances (have BaseType pointing to a trait, or computed properties)
        var allTypeDecls = modules.SelectMany(m => m.Module.Declarations)
            .Concat(moduleLoader.LoadedModules.SelectMany(m => m.Declarations))
            .OfType<Cop.Lang.Ast.TypeDecl>();
        var traitCandidates = allTypeDecls.Where(td =>
            td.Properties.Any(p => p.ComputedExpr is not null || p.Type.Name.Contains("=>"))).ToList();
        // Load all trait-related types in one batch so conformance registration works
        var traitTypeDefs = traitCandidates.Select(td =>
        {
            var props = td.Properties.Select(p =>
                new PropertyDefinition(p.Name, p.Type.Name, p.IsOptional, p.Type.IsCollection, p.Line, p.ComputedExpr)).ToList();
            return new TypeDefinition(td.Name, td.BaseType, props, 0, td.IsExported, td.DocComment, td.Traits);
        }).ToList();
        typeRegistry.LoadTypeDefinitions(traitTypeDefs);
        bridge.Evaluator.TypeRegistry = typeRegistry;

        var providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        var seenProviderDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Track provider entry points from explicit -p flags so import-detected duplicates are skipped
        var explicitProviderEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (topLevelProviderPackages is not null)
        {
            foreach (var providerPackage in topLevelProviderPackages)
            {
                if (seenProviderDirs.Add(providerPackage.Dir))
                {
                    providerPackages.Add(providerPackage);
                    if (!string.IsNullOrEmpty(providerPackage.Meta.ProviderEntry))
                        explicitProviderEntries.Add(providerPackage.Meta.ProviderEntry);
                }
            }
        }

        foreach (var (dir, _) in moduleLoader.ProviderPackages)
        {
            diagLog?.Invoke($"[diag] Discovered provider package: {dir}");
            var metadata = PackageMetadata.TryLoadFromDirectory(dir);
            if (metadata is not null && metadata.IsProvider && seenProviderDirs.Add(dir))
            {
                // Skip if same provider entry point was already loaded from an explicit -p flag
                if (!string.IsNullOrEmpty(metadata.ProviderEntry) && explicitProviderEntries.Contains(metadata.ProviderEntry))
                {
                    diagLog?.Invoke($"[diag] Skipping import-detected provider '{metadata.Name}' (entry '{metadata.ProviderEntry}' already loaded from -p flag)");
                    continue;
                }
                providerPackages.Add((dir, metadata));
            }
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
            // Register the provider's schema types (and accessors) into the TypeRegistry so
            // trait/subtype resolution works for provider-declared types — e.g. so a
            // language-specific subtype like CSharpType is known to be a subtype of Type.
            ProviderLoader.RegisterSchema(loaded.Instance, typeRegistry);
            var runtimeBindings = loaded.Instance.GetRuntimeBindings();
            RegisterLazyProviderCollections(bridge.Evaluator.GlobalEnvironment, loaded.PackageName, loaded.Instance, loaded.Schema, query, errors, warnings, runtimeBindings, diagLog);

            // Register provider functions (e.g., http.Post) as qualified callables: <package>.<fn>.
            // Resolved through CopProviderProxy.GetField (which looks up "<package>.<fn>" in the env).
            var providerFunctions = loaded.Instance.GetFunctions();
            if (providerFunctions is not null)
            {
                foreach (var (fnName, fn) in providerFunctions)
                {
                    var capturedFn = fn;
                    var qualifiedName = $"{loaded.PackageName}.{fnName}";
                    bridge.Evaluator.GlobalEnvironment.Define(qualifiedName,
                        new CopExternalFunction(qualifiedName, (callArgs, _) => InvokeProviderFunction(capturedFn, callArgs)));
                }
            }
        }

        foreach (var builtinProvider in _builtinProviders)
        {
            queryService.RegisterProvider(builtinProvider.Name, builtinProvider.Instance, builtinProvider.Schema);
            // Lazy: a built-in provider (e.g. filesystem / markdown parsing) is only
            // queried when one of its collections is actually accessed. Programs that
            // never use a provider pay nothing for it.
            var runtimeBindings = builtinProvider.Instance.GetRuntimeBindings();
            RegisterLazyProviderCollections(bridge.Evaluator.GlobalEnvironment, builtinProvider.Name, builtinProvider.Instance, builtinProvider.Schema, query, errors, warnings, runtimeBindings, diagLog);
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
                return new CopQueryable(providerName, providerQuery, queryService.QueryProvider);
            }

            return new CopProviderProxy(providerName, env);
        });

        // Register Program with Providers list — only explicit -p providers, not import-detected ones.
        // Import-detected providers are loaded for provider('name') intrinsic access,
        // but should NOT appear in Program.Providers (the user didn't request them for analysis).
        var providerProxies = (topLevelProviderPackages ?? [])
            .Select(pp => PackageMetadata.TryLoadFromDirectory(pp.Dir)?.Name ?? Path.GetFileName(pp.Dir))
            .Where(name => bridge.Evaluator.GlobalEnvironment.TryLookup(name, out _))
            .Select(name => (CopValue)new CopProviderProxy(name, bridge.Evaluator.GlobalEnvironment))
            .ToList();
        RegisterProgram(bridge, programArgs, providerProxies);

        // Phase 2: Evaluate let bindings (now that provider data is available)
        // First: deferred let bindings from imported packages
        moduleLoader.EvalDeferredLetBindings(bridge.Evaluator, errors);

        // Save original callable bindings (functions from imports) before user lets.
        // Multiple sibling modules may define `let X = X(...)` — the first module replaces
        // the function with its result, so subsequent modules need the original callable
        // restored temporarily when evaluating their RHS.
        var originalCallables = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (var (name, value) in bridge.Evaluator.GlobalEnvironment.AllBindings())
        {
            if (value is ICopCallable)
                originalCallables[name] = value;
        }

        // Then: user module let bindings
        foreach (var module in modules)
        {
            try
            {
                bridge.Evaluator.EvalLetBindings(module.Module, originalCallables);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add(ex.Message);
            }
        }

        // When multiple top-level packages are specified, register each package's 'command main'
        // under a package-qualified alias so all can be run in sequence.
        // Note: the parser converts `command foo` to FunctionDecl with name uppercased to "FOO".
        if (packageModuleMap is { Count: > 1 })
        {
            foreach (var (pkgName, pkgModules) in packageModuleMap)
            {
                bool found = false;
                foreach (var mod in pkgModules)
                {
                    var mainFunc = mod.Module.Declarations
                        .OfType<FunctionDecl>()
                        .FirstOrDefault(fd => string.Equals(fd.Name, "MAIN", StringComparison.OrdinalIgnoreCase));
                    if (mainFunc is not null)
                    {
                        var qualifiedName = NormalizeCommandName(pkgName);
                        var cmdFunc = new CopFunction(mainFunc, bridge.Evaluator.GlobalEnvironment);
                        bridge.Evaluator.GlobalEnvironment.Define(qualifiedName, cmdFunc);
                        diagLog?.Invoke($"[diag] Registered package command '{qualifiedName}' from {pkgName}");
                        found = true;
                        break;
                    }
                }
                if (!found)
                    diagLog?.Invoke($"[diag] No 'command main' found in package '{pkgName}'");
            }
        }

        if (ProfileRules)
            ProfileAllRules(bridge, setupSw.ElapsedMilliseconds);

        List<string> commandsToRun;
        if (assertMode)
        {
            // Test mode (`cop test`): run every TEST-* command — each evaluates an
            // assert() call that records a result. There is no 'main' to run.
            // `test foo = ...` desugars to a FunctionDecl named TEST-FOO.
            commandsToRun = modules
                .SelectMany(m => m.Module.Declarations)
                .Select(d => d switch
                {
                    FunctionDecl fd => fd.Name,
                    CommandDecl cd => cd.Name,
                    _ => null
                })
                .Where(n => n is not null && n.StartsWith("TEST-", StringComparison.Ordinal))
                .Select(n => n!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            commandsToRun = commandFilter is { Length: > 0 }
                ? commandFilter.Select(NormalizeCommandName).Distinct(StringComparer.Ordinal).ToList()
                : packageModuleMap is { Count: > 1 }
                    ? packageModuleMap.Keys.Select(NormalizeCommandName).ToList()
                    : new List<string> { commandName ?? "main" };
        }

        int? exitCode = null;
        foreach (var command in commandsToRun)
        {
            try
            {
                diagLog?.Invoke($"[diag] Running command '{command}'");
                var result = bridge.RunCommand(command);
                diagLog?.Invoke($"[diag] Command '{command}' returned: {result?.GetType().Name} = {result?.Display()?.Substring(0, Math.Min(result?.Display()?.Length ?? 0, 100))}");

                // If command returns an integer, use it as the exit code
                if (result is CopInt exitInt)
                {
                    exitCode = exitInt.Value;
                }
                else
                {
                    // If a command returns a collection (e.g., let-binding used as a named rule),
                    // iterate items and produce output for each.
                    CollectCollectionOutputs(result, outputs);
                }
                diagLog?.Invoke($"[diag] After collect, outputs count = {outputs.Count}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"Command '{command}' failed: {ex.Message}");
                diagLog?.Invoke($"[diag] Command exception: {ex}");
            }
        }

        // Check for "Command not found" errors and enhance with exported member listing
        errors.AddRange(bridge.Errors);
        var notFoundErrors = errors.Where(e => e.Contains("not found") && e.Contains("Command")).ToList();
        if (notFoundErrors.Count > 0)
        {
            var exportedMembers = GetExportedMembers(modules);
            if (exportedMembers.Count > 0)
            {
                foreach (var nfe in notFoundErrors)
                    errors.Remove(nfe);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Package has no 'command main'. Exported functions:");
                foreach (var member in exportedMembers)
                    sb.AppendLine($"  {member}");
                errors.Add(sb.ToString().TrimEnd());
            }
        }
        else
        {
            // No command-not-found — just add bridge errors normally
            // (already added above, so no-op for the else case)
        }

        // Collect structured diagnostics from module loader
        var diagnostics = new List<CopDiagnostic>();
        diagnostics.AddRange(moduleLoader.Diagnostics);

        var fileOutputs = fileOutputLines
            .Select(kv => new FileOutput(kv.Key, string.Join(System.Environment.NewLine, kv.Value)))
            .ToList();

        string? resultCommandName = commandFilter is { Length: > 0 }
            ? commandsToRun.Count == 1 ? commandsToRun[0] : null
            : commandName ?? "main";

        return new EngineResult(outputs, parseErrors, errors, resultCommandName, fileOutputs, warnings, asserts, diagnostics, exitCode);
    }

    /// <summary>
    /// Times the force+enumerate cost of every top-level rule (let-binding that
    /// evaluates to a collection) and prints a `[profile]` breakdown to stderr.
    /// Leaf rules are forced before aggregate ("all-*") rules so the aggregates are
    /// not charged the cost of the leaves they reference (thunks memoize once forced).
    /// AI rules are skipped because forcing them would make a network call.
    /// </summary>
    private static void ProfileAllRules(LanguageBridge bridge, long setupMs)
    {
        var evaluator = bridge.Evaluator;
        var savedTrace = evaluator.TraceLog;
        evaluator.TraceLog = null; // exclude trace logging overhead from measurements

        // AI rules make network calls (ai.judge) — never force them while profiling.
        var skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "all-ai-violations",
            "core-purity-violations",
        };

        var thunks = evaluator.GlobalEnvironment.AllBindings()
            .Where(b => b.Value is CopThunk && !skip.Contains(b.Key))
            .Select(b => (Name: b.Key, Thunk: (CopThunk)b.Value))
            // Leaves first (rules), aggregates ("all-*") last.
            .OrderBy(t => t.Name.StartsWith("all-", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var results = new List<(string Name, double Ms, int Count, string? Error)>();
        var sw = new Stopwatch();
        foreach (var (name, thunk) in thunks)
        {
            sw.Restart();
            try
            {
                var count = CountForced(thunk.Force());
                sw.Stop();
                results.Add((name, sw.Elapsed.TotalMilliseconds, count, null));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sw.Stop();
                results.Add((name, sw.Elapsed.TotalMilliseconds, -1, ex.Message));
            }
        }

        evaluator.TraceLog = savedTrace;

        var ruleTotal = results.Sum(r => r.Ms);
        Console.Error.WriteLine();
        Console.Error.WriteLine($"[profile] setup (parse + load providers): {setupMs} ms");
        Console.Error.WriteLine($"[profile] rules total: {ruleTotal:F1} ms across {results.Count} rules");
        Console.Error.WriteLine($"[profile] {"ms",8}  {"items",6}  rule");
        foreach (var r in results.OrderByDescending(r => r.Ms).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            var items = r.Count < 0 ? "ERR" : r.Count.ToString();
            var err = r.Error is null ? "" : "  -- " + r.Error;
            Console.Error.WriteLine($"[profile] {r.Ms,8:F2}  {items,6}  {r.Name}{err}");
        }
        Console.Error.WriteLine();
    }

    /// <summary>Forces and counts the items of a (possibly lazy) collection value.</summary>
    private static int CountForced(CopValue v)
    {
        if (v is CopThunk t) v = t.Force();
        return v switch
        {
            CopList l => l.Items.Count,
            CopLazyCollection lz => lz.Enumerate().Count(),
            CopQueryable q => q.Enumerate().Count(),
            _ => 0,
        };
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

    // Invokes a provider function (from ObjectProvider.GetFunctions): marshals cop args → CLR,
    // synchronously awaits the Task, and marshals the CLR result → CopValue.
    private static CopValue InvokeProviderFunction(Func<List<object?>, Task<object?>> fn, IReadOnlyList<CopValue> args)
    {
        var clrArgs = args.Select(CopToClr).ToList();
        var result = fn(clrArgs).GetAwaiter().GetResult();
        return DataObjectAdapter.Marshal(result);
    }

    // Converts a CopValue to a plain CLR value for passing into a provider function.
    private static object? CopToClr(CopValue value)
    {
        while (value is CopThunk thunk) value = thunk.Force();
        return value switch
        {
            CopNull => null,
            CopString s => s.Value,
            CopInt i => i.Value,
            CopNumber n => n.Value,
            CopBool b => b.Value,
            CopList l => l.Items.Select(CopToClr).ToList(),
            CopLazyCollection lazy => lazy.Enumerate().Select(CopToClr).ToList(),
            CopQueryable q => q.Enumerate().Select(CopToClr).ToList(),
            CopDynamicObject d => d.Underlying,
            CopObject o => CopObjectToDataObject(o),
            _ => value.Display()
        };
    }

    private static DataObject CopObjectToDataObject(CopObject obj)
    {
        var data = new DataObject(obj.TypeName ?? "Object");
        foreach (var (key, val) in obj.Fields)
            data.Set(key, CopToClr(val));
        return data;
    }

    private static void RegisterProgram(LanguageBridge bridge, string[]? programArgs, List<CopValue>? providers = null)
    {
        var args = (programArgs ?? [])
            .Select(arg => (CopValue)new CopString(arg))
            .ToList();
        var programObj = new CopObject(new Dictionary<string, CopValue>(StringComparer.Ordinal)
        {
            ["Args"] = new CopList(args),
            ["Providers"] = new CopList(providers ?? [])
        }) { TypeName = "Program" };

        // Register as both a value (Program) and FFI function (program())
        bridge.RegisterValue("Program", programObj);
        bridge.RegisterFunction("program", (_, _) => programObj, 0);
    }

    private static void WarnIfProviderEmpty(string providerName, Dictionary<string, List<object>> collections, ProviderSchema schema, List<string> warnings)
    {
        // Skip providers that don't declare any collections (e.g., pure schema providers)
        if (schema.Collections.Count == 0)
            return;

        // Skip when dict is empty — means provider returned null (schema-only, data comes from external providers)
        if (collections.Count == 0)
            return;

        // Skip for analysis/tool providers — 0 violations means clean code, not a failure
        if (schema.Collections.All(c => c.Name == "Violations" || c.ItemType == "Violation"))
            return;

        // Check if ALL collections returned 0 items
        int totalItems = 0;
        foreach (var (_, items) in collections)
            totalItems += items.Count;

        if (totalItems == 0)
        {
            // A provider that returns 0 items means checks cannot produce reliable results.
            warnings.Add($"Error: Provider '{providerName}' returned 0 items across all collections. " +
                $"This likely indicates a transient filesystem issue (antivirus, file locks). " +
                $"Check results are unreliable — treat this run as failed.");
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

    /// <summary>
    /// Registers lazy collections for a provider. Data is not loaded until a collection
    /// is first accessed, avoiding expensive provider queries (e.g., Roslyn parsing)
    /// for unused providers.
    /// </summary>
    private static void RegisterLazyProviderCollections(
        Cop.Lang.Interpreter.Environment env, string providerName,
        DataProvider provider, ProviderSchema schema, ProviderQuery query,
        List<string> errors, List<string> warnings,
        RuntimeBindings? bindings, Action<string>? diagLog)
    {
        // Shared lazy: all collections for this provider are loaded together on first access
        var lazyData = new Lazy<Dictionary<string, List<object>>>(() =>
        {
            var collections = QueryProviderCollections(provider, schema, query, errors);
            diagLog?.Invoke($"[diag] Provider '{providerName}' returned {collections.Count} collections: {string.Join(", ", collections.Select(c => $"{c.Key}({c.Value.Count})"))}");
            WarnIfProviderEmpty(providerName, collections, schema, warnings);
            return collections;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        var typeSchemas = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var collectionItemTypes = schema.Collections.ToDictionary(c => c.Name, c => c.ItemType, StringComparer.Ordinal);

        foreach (var collection in schema.Collections)
        {
            var collName = collection.Name;
            var lazy = new CopLazyCollection(() =>
            {
                var allCollections = lazyData.Value;
                if (!allCollections.TryGetValue(collName, out var items) || items.Count == 0)
                    return [];

                if (collectionItemTypes.TryGetValue(collName, out var itemType))
                {
                    if (items[0] is RecordView && typeSchemas.TryGetValue(itemType, out var typeSchema))
                    {
                        var rvAdapter = new RecordViewAdapter(typeSchema.Properties, typeName: itemType);
                        return items.Select(item => (CopValue)new CopDynamicObject(item, rvAdapter));
                    }

                    if (items[0] is not DataObject && bindings?.Accessors is not null)
                    {
                        // Per-item adapter selection. A collection declared as [Type] may contain
                        // language-specific subtypes (e.g. CSharpType items in a Types collection)
                        // whose CLR type maps to a different cop type than the collection's declared
                        // itemType. Resolving each item's adapter by its CLR type mapping lets
                        // subtype-only fields (e.g. CSharpType.IsRecord) resolve to real provider
                        // data instead of null, while plain items keep the collection's default.
                        var runtimeBindings = bindings;
                        var defaultAdapter = runtimeBindings.Accessors.TryGetValue(itemType, out var accessors)
                            ? new ClrObjectAdapter(accessors, typeName: itemType,
                                allAccessors: runtimeBindings.Accessors, clrTypeMappings: runtimeBindings.ClrTypeMappings)
                            : null;
                        var adapterCache = new Dictionary<string, ClrObjectAdapter>(StringComparer.Ordinal);
                        return items.Select(item =>
                        {
                            var itemAdapter = ResolveClrItemAdapter(item, itemType, defaultAdapter, adapterCache, runtimeBindings);
                            return (CopValue)new CopDynamicObject(item, itemAdapter ?? (IDynamicObjectAdapter)DataObjectAdapter.Instance);
                        });
                    }
                }

                return items.Select(item => (CopValue)new CopDynamicObject(item, DataObjectAdapter.Instance));
            });

            env.Define($"{providerName}.{collName}", lazy);
        }

        env.Define(providerName, new CopProviderProxy(providerName, env));
    }

    /// <summary>
    /// Resolves the adapter for a single provider collection item. When the item's CLR type
    /// maps (via the provider's <see cref="RuntimeBindings.ClrTypeMappings"/>) to a different
    /// cop type than the collection's declared <paramref name="itemType"/> — e.g. a
    /// language-specific subtype like CSharpType inside a [Type] collection — the item gets
    /// that subtype's accessor set so its extra fields resolve to real data. Otherwise it
    /// uses the collection's <paramref name="defaultAdapter"/>. Adapters are cached per
    /// resolved type name to avoid re-allocating one per item.
    /// </summary>
    private static ClrObjectAdapter? ResolveClrItemAdapter(
        object item, string itemType, ClrObjectAdapter? defaultAdapter,
        Dictionary<string, ClrObjectAdapter> cache, RuntimeBindings bindings)
    {
        if (bindings.ClrTypeMappings.TryGetValue(item.GetType(), out var mapped)
            && !string.Equals(mapped, itemType, StringComparison.Ordinal)
            && bindings.Accessors.TryGetValue(mapped, out var accessors))
        {
            if (!cache.TryGetValue(mapped, out var adapter))
            {
                adapter = new ClrObjectAdapter(accessors, typeName: mapped,
                    allAccessors: bindings.Accessors, clrTypeMappings: bindings.ClrTypeMappings);
                cache[mapped] = adapter;
            }
            return adapter;
        }
        return defaultAdapter;
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

    private static List<string> GetExportedMembers(List<ParsedModule> modules)
    {
        var members = new List<string>();
        foreach (var mod in modules)
        {
            foreach (var decl in mod.Module.Declarations)
            {
                if (decl is FunctionDecl fd && fd.IsExported && !string.Equals(fd.Name, "MAIN", StringComparison.OrdinalIgnoreCase))
                {
                    var paramsStr = string.Join(", ", fd.Params.Select(p =>
                        p.Type is not null ? $"{p.Name} : {FormatTypeRef(p.Type)}" : p.Name));
                    var returnStr = fd.ReturnType is not null ? $" : {FormatTypeRef(fd.ReturnType)}" : "";
                    members.Add($"{fd.Name}({paramsStr}){returnStr}");
                }
                else if (decl is LetDecl ld && ld.IsExported)
                {
                    var typeStr = ld.TypeAnnotation is not null ? $" : {FormatTypeRef(ld.TypeAnnotation)}" : "";
                    members.Add($"{ld.Name}{typeStr}");
                }
            }
        }
        return members;
    }

    private static string FormatTypeRef(TypeRef t) =>
        t.IsCollection ? $"[{t.Name}]" : t.Name;

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
    /// Finds packages/ feed paths (global cache + walking up from scriptsDir).
    /// Delegates to PackageResolver.GetFeedPaths for consistency with verify.
    /// </summary>
    private static List<string> FindFeedPaths(string scriptsDir) =>
        Cop.Cli.Commands.PackageResolver.GetFeedPaths(scriptsDir);

    /// <summary>
    /// Runs packages from feeds: loads packages by name, executes selected rules.
    /// </summary>
    public static EngineResult RunProject(
        List<string> feedPaths,
        List<string> packageNames,
        string rootPath,
        List<string> rules,
        string[]? programArgs = null,
        Action<string>? diagLog = null,
        string[]? additionalScriptFiles = null,
        string[]? providers = null)
    {
        rootPath = Path.GetFullPath(rootPath);

        var parseErrors = new List<string>();
        var fatalErrors = new List<string>();
        var modules = new List<ParsedModule>();
        var providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        var packageModuleMap = new Dictionary<string, List<ParsedModule>>(StringComparer.OrdinalIgnoreCase);
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
                    var parsed = ParseModules(copFiles, parseErrors);
                    modules.AddRange(parsed);
                    packageModuleMap[packageName] = parsed;
                }

                DetectProviderPackage(Path.Combine(packageDir, "src"), packageName, normalizedFeedPaths, providerPackages, parseErrors);
                found = true;
                break;
            }

            if (!found)
                fatalErrors.Add($"Package '{packageName}' not found in any feed");
        }

        // Include additional script files (e.g., user-global checks)
        if (additionalScriptFiles is { Length: > 0 })
            modules.AddRange(ParseModules(additionalScriptFiles, parseErrors));

        // Resolve -p provider packages
        if (providers is { Length: > 0 })
        {
            foreach (var providerName in providers)
            {
                string? pkgDir = null;
                foreach (var feedPath in normalizedFeedPaths)
                {
                    pkgDir = ImportResolver.FindPackageDir(feedPath, providerName);
                    if (pkgDir != null) break;
                }
                if (pkgDir is null)
                {
                    fatalErrors.Add($"Provider package '{providerName}' not found in any feed");
                    continue;
                }
                var metadata = PackageMetadata.TryLoadFromDirectory(pkgDir);
                if (metadata is not null && metadata.IsProvider)
                    providerPackages.Add((pkgDir, metadata));
                else
                    fatalErrors.Add($"Package '{providerName}' is not a provider (no lib/ with DLL)");
            }
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
            topLevelProviderPackages: providerPackages,
            packageModuleMap: packageModuleMap.Count > 1 ? packageModuleMap : null);
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
                    var props = typeDecl.Properties.Select(p => new PropertyDefinition(p.Name, p.Type.Name, p.IsOptional, p.Type.IsCollection, p.Line, p.ComputedExpr)).ToList();
                    typeRegistry.LoadTypeDefinitions([new TypeDefinition(typeDecl.Name, typeDecl.BaseType, props, 0, typeDecl.IsExported, typeDecl.DocComment, typeDecl.Traits)]);
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
    List<AssertResult>? Asserts = null,
    List<CopDiagnostic>? Diagnostics = null,
    int? ExitCode = null)
{
    public bool HasParseErrors => ParseErrors.Count > 0;
    public bool HasFatalErrors => Errors.Count > 0;
    public bool IsCommandMode => CommandName != null;
}
