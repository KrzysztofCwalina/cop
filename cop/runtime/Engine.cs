// Coordinates program loading, provider registration, evaluation, and execution results.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
    private static readonly DataProvider[] s_rawProviders =
    [
        new FilesystemProvider(),
        new CodeSchemaProvider(),
        new Markdown.MarkdownProvider(),
    ];

    private record BuiltinProvider(string Name, DataProvider Instance, ProviderSchema Schema, HashSet<string> CollectionNames);

    public sealed record ParsedModule(string FilePath, string Source, ModuleNode Module);

    private static readonly BuiltinProvider[] s_builtinProviders = s_rawProviders.Select(ToBuiltin).ToArray();

    /// <summary>
    /// When true, the engine times each top-level rule (let-binding) after provider
    /// data is loaded and prints a <c>[profile]</c> breakdown to stderr before running
    /// commands. Enabled via the CLI <c>-rp</c> flag.
    /// </summary>
    public static bool IsRuleProfilingEnabled { get; set; }

    private static BuiltinProvider ToBuiltin(DataProvider provider)
    {
        ProviderSchema schema = ProviderSchema.FromJson(provider.GetSchema());
        HashSet<string> collNames = new HashSet<string>(schema.Collections.Select(c => c.Name), StringComparer.Ordinal);
        string name = provider switch
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
    public static async Task<EngineResult> RunAsync(
        string scriptsDir, string rootPath, EngineRunOptions? options = null)
    {
        options ??= new EngineRunOptions();
        options.CancellationToken.ThrowIfCancellationRequested();
        Stopwatch totalSw = Stopwatch.StartNew();

        scriptsDir = Path.GetFullPath(scriptsDir);
        rootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(scriptsDir))
        {
            return new EngineResult([], [], [$"Scripts directory not found: {scriptsDir}"]);
        }

        string[] scriptFilePaths = options.ScriptFiles?.ToArray()
            ?? Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        // Only sort when discovering files ourselves. When scriptFiles is explicitly provided,
        // respect the caller's order (e.g., target file placed last for command priority).
        if (options.ScriptFiles is null)
        {
            Array.Sort(scriptFilePaths, StringComparer.Ordinal);
        }

        if (scriptFilePaths.Length == 0)
        {
            return new EngineResult([], [], []);
        }

        List<string> parseErrors = new List<string>();
        List<ParsedModule> modules = ParseModules(scriptFilePaths, parseErrors);
        if (modules.Count == 0 && parseErrors.Count > 0)
        {
            return new EngineResult([], parseErrors, []);
        }

        List<string> feedPaths = CollectFeedPaths(
            scriptsDir, modules, options.AdditionalFeedPaths?.ToArray());
        EngineResult result = await ExecuteModulesAsync(
            modules,
            feedPaths,
            rootPath,
            parseErrors,
            options);

        options.DiagnosticLog?.Invoke($"[diag] Total: {totalSw.ElapsedMilliseconds}ms");
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
        List<ParsedModule> modules = new List<ParsedModule>();
        foreach (string path in filePaths)
        {
            try
            {
                string source = File.ReadAllText(path);
                modules.Add(new ParsedModule(path, source, CopParser.Parse(source, path)));
            }
            catch (ParseException ex)
            {
                parseErrors.Add(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
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
        List<ParsedModule> modules = new List<ParsedModule>();
        foreach (string path in filePaths)
        {
            try
            {
                string source = File.ReadAllText(path);
                modules.Add(new ParsedModule(path, source, CopParser.Parse(source, path)));
            }
            catch (ParseException ex)
            {
                parseErrors.Add(ex.Message);
                diagnostics.Add(ex.ToDiagnostic());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
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
        List<string> feedPaths = FindFeedPaths(scriptsDir);
        HashSet<string> seen = new HashSet<string>(feedPaths, StringComparer.OrdinalIgnoreCase);

        foreach (ParsedModule module in modules)
        {
            string scriptDir = Path.GetDirectoryName(module.FilePath) ?? scriptsDir;
            foreach (string feedPath in ModuleLoader.ExtractFeedPaths(module.Source, scriptDir))
            {
                if (seen.Add(feedPath))
                {
                    feedPaths.Add(feedPath);
                }
            }
        }

        if (additionalFeedPaths is not null)
        {
            foreach (string feedPath in additionalFeedPaths)
            {
                string resolved = Path.GetFullPath(feedPath);
                if (Directory.Exists(resolved) && seen.Add(resolved))
                {
                    feedPaths.Add(resolved);
                }
            }
        }

        // Explicit and caller-supplied feeds must also precede the restored-package fallback.
        string globalCache = Cop.Cli.Commands.PackageResolver.GlobalCachePath;
        return feedPaths.OrderBy(path =>
            string.Equals(path, globalCache, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void ResolveImportsRetainingCallerFunctions(
        ModuleLoader loader, IEnumerable<ModuleNode> modules, Evaluator evaluator)
    {
        List<(string Name, CopValue Original, CopFunctionGroup Group)> callerFunctions = [];
        foreach ((string name, CopValue value) in evaluator.GlobalEnvironment.AllBindings()
            .ToArray())
        {
            CopFunctionGroup group;
            if (value is CopFunction function)
            {
                group = new CopFunctionGroup(name);
                group.Add(function);
            }
            else if (value is CopFunctionGroup existing)
            {
                group = existing;
            }
            else
            {
                continue;
            }

            // Retain imported overloads even if an enum later overwrites the global slot.
            callerFunctions.Add((name, value, group));
            evaluator.GlobalEnvironment.Define(name, group);
        }

        loader.ResolveImports(modules, evaluator);

        // Imported enum and flag members must not replace caller-defined functions.
        foreach ((string name, CopValue original, CopFunctionGroup group) in callerFunctions)
        {
            CopValue current = evaluator.GlobalEnvironment.Lookup(name);
            if (current is CopString or CopInt || ReferenceEquals(current, group))
            {
                evaluator.GlobalEnvironment.Define(name,
                    group.Functions.Count == 1 ? original : group);
            }
        }
    }

    private static async Task<EngineResult> ExecuteModulesAsync(
        List<ParsedModule> modules,
        List<string> feedPaths,
        string rootPath,
        List<string> parseErrors,
        EngineRunOptions options)
    {
        string? commandName = options.CommandName;
        string[]? programArgs = options.ProgramArgs?.ToArray();
        string[]? commandFilter = options.CommandFilter?.ToArray();
        Action<string>? diagLog = options.DiagnosticLog;
        List<(string Dir, PackageMetadata Meta)>? topLevelProviderPackages =
            options.ProviderPackages?.ToList();
        bool assertMode = options.IsAssertMode;
        Dictionary<string, List<ParsedModule>>? packageModuleMap = options.PackageModules;
        ReviewOptions? reviewOptions = options.ReviewOptions;
        options.CancellationToken.ThrowIfCancellationRequested();
        using ExecutionStatistics statistics = new ExecutionStatistics();
        List<PrintOutput> outputs = new List<PrintOutput>();
        List<string> warnings = new List<string>();
        List<string> errors = new List<string>();
        List<AssertResult> asserts = new List<AssertResult>();
        Dictionary<string, List<string>> fileOutputLines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Stopwatch setupSw = Stopwatch.StartNew();

        // Reset the provider-error sink for this run. Providers (which share this static) report a
        // file that failed to parse here; it is drained below and surfaced — providers must not hide
        // errors behind an incomplete or partial model.
        Cop.Core.ProviderErrors.Clear();
        Cop.Core.ProviderWarnings.Clear();
        await using ToolchainExecutionScope toolchainScope = new ToolchainExecutionScope();

        LanguageBridge bridge = CreateBridge(outputs, fileOutputLines, asserts, diagLog);
        bridge.RegisterFunction("observe", (args, _) =>
        {
            if (args.Count != 3 || args[1] is not CopString category || args[2] is not CopString name)
                throw new ArgumentException("observe requires a value, a category string, and a name string.");
            statistics.RecordEvaluation(category.Value, name.Value);
            return args[0];
        }, 3);
        ConcurrentDictionary<LetDecl, byte> evaluatedBindings = new System.Collections.Concurrent.ConcurrentDictionary<LetDecl, byte>(ReferenceEqualityComparer.Instance);
        LeafCheckCatalog? leafChecks = null;
        bridge.Evaluator.LetBindingEvaluated = declaration =>
        {
            if (evaluatedBindings.TryAdd(declaration, 0))
            {
                statistics.CommandCompleted();
            }

            if (leafChecks?.NameOf(declaration) is { } check)
            {
                statistics.RecordEvaluation("check", check);
            }
        };
        bridge.Evaluator.UserFunctionCalled = declaration =>
        {
            if (leafChecks?.NameOf(declaration) is { } check)
            {
                statistics.RecordEvaluation("check", check);
            }
        };
        bridge.Evaluator.EntryCommandCompleted = statistics.CommandCompleted;
        AnalysisContext context;
        try
        {
            context = new AnalysisContext(rootPath, reviewOptions ?? new ReviewOptions(), Console.Error.WriteLine);
            if (context.Options.Scope is "branch" or "pr" || context.Options.ReportScope == "changes")
            {
                _ = await context.GetChangesAsync(options.CancellationToken);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return new EngineResult([], parseErrors, [$"Review scope: {ex.Message}"]);
        }
        ChangeFunctions.Register(bridge, context);
        // Per-item evaluator trace ([trace] lines) is a deep-debug firehose (millions of lines on
        // large repos). Enabled only at -ddd (CopDiagnostics.Trace); -d/-dd stay focused on [diag].
        bridge.Evaluator.TraceLog =
            (diagLog is not null && CopDiagnostics.IsTraceEnabled) ? diagLog : null;

        // Phase 1: Register functions, types, enums (NOT let bindings — those need provider data)
        foreach (ParsedModule module in modules)
        {
            try
            {
                bridge.Evaluator.RegisterDeclarations(module.Module);
            }
            catch (CopEvaluationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        ModuleLoader moduleLoader = new ModuleLoader(feedPaths);
        try
        {
            ResolveImportsRetainingCallerFunctions(
                moduleLoader, modules.Select(m => m.Module), bridge.Evaluator);
        }
        catch (CopEvaluationException ex)
        {
            errors.Add(ex.Message);
        }

        // Auto-restore already ran in the CLI. Imports still unresolved here must fail,
        // even when a command does not reference the missing package's exports.
        errors.AddRange(moduleLoader.Errors);

        // Validate user modules (same checks as 'cop verify') before execution.
        // This catches issues like enum-vs-string comparisons that would silently fail at runtime.
        // Collect external symbols excluding user-declared names (those are already in the modules being bound).
        HashSet<string> userDeclaredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParsedModule module in modules)
        {
            foreach (Declaration decl in module.Module.Declarations)
            {
                string? declName = decl switch
                {
                    TypeDecl td => td.Name,
                    EnumDecl ed => ed.Name,
                    FlagsDecl fd => fd.Name,
                    FunctionDecl fd => fd.Name,
                    LetDecl ld => ld.Name,
                    CommandDecl cd => cd.Name,
                    _ => null
                };
                if (declName != null)
                {
                    userDeclaredNames.Add(declName);
                }
            }
        }

        List<Symbol> externalSymbols = Cop.Cli.Commands.VerifyCommand.CollectExternalSymbols(bridge.Evaluator.GlobalEnvironment, moduleLoader.LoadedModules, s_builtinProviders.Select(p => p.Schema));
        externalSymbols.RemoveAll(s => userDeclaredNames.Contains(s.Name));
        foreach (ParsedModule module in modules)
        {
            Binder binder = new Binder(module.FilePath, externalSymbols);
            BindingResult bindingResult = binder.Bind(module.Module);
            foreach (BindingDiagnostic diag in bindingResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                {
                    errors.Add($"{diag.FilePath ?? module.FilePath}({diag.Line}): error: {diag.Message}");
                }
                else
                {
                    // Binder warnings/info were silently dropped; surface them so they reach the user.
                    warnings.Add($"{diag.FilePath ?? module.FilePath}({diag.Line}): {(diag.Severity == DiagnosticSeverity.Warning ? "warning" : "info")}: {diag.Message}");
                }
            }
        }
        if (errors.Count > 0)
        {
            return new EngineResult(outputs, parseErrors, errors);
        }

        // Build TypeRegistry for trait dispatch and computed property resolution
        TypeRegistry typeRegistry = new TypeRegistry();
        foreach (BuiltinProvider bp in s_builtinProviders)
        {
            ProviderLoader.RegisterSchema(bp.Instance, typeRegistry);
        }
        // Only load types that define traits or conformances (have BaseType pointing to a trait, or computed properties)
        IEnumerable<TypeDecl> allTypeDecls = modules.SelectMany(m => m.Module.Declarations)
            .Concat(moduleLoader.LoadedModules.SelectMany(m => m.Declarations))
            .OfType<Cop.Lang.Ast.TypeDecl>();
        List<TypeDecl> traitCandidates = allTypeDecls.Where(td =>
            td.Properties.Any(p => p.ComputedExpr is not null || p.Type.Name.Contains("=>"))).ToList();
        // Load all trait-related types in one batch so conformance registration works
        List<TypeDefinition> traitTypeDefs = traitCandidates.Select(td =>
        {
            List<PropertyDefinition> props = td.Properties.Select(p =>
                new PropertyDefinition(p.Name, p.Type.Name, p.IsOptional, p.Type.IsCollection, p.Line, p.ComputedExpr)).ToList();
            return new TypeDefinition(td.Name, td.BaseType, props, 0, td.IsExported, td.DocComment, td.Traits);
        }).ToList();
        typeRegistry.LoadTypeDefinitions(traitTypeDefs);
        bridge.Evaluator.TypeRegistry = typeRegistry;

        List<(string Dir, PackageMetadata Meta)> providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        HashSet<string> seenProviderDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Track provider entry points from explicit -p flags so import-detected duplicates are skipped
        HashSet<string> explicitProviderEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (topLevelProviderPackages is not null)
        {
            foreach ((string Dir, PackageMetadata Meta) providerPackage in topLevelProviderPackages)
            {
                if (seenProviderDirs.Add(providerPackage.Dir))
                {
                    providerPackages.Add(providerPackage);
                    if (!string.IsNullOrEmpty(providerPackage.Meta.ProviderEntry))
                    {
                        explicitProviderEntries.Add(providerPackage.Meta.ProviderEntry);
                    }
                }
            }
        }

        foreach ((string? dir, string _) in moduleLoader.ProviderPackages)
        {
            diagLog?.Invoke($"[diag] Discovered provider package: {dir}");
            PackageMetadata? metadata = PackageMetadata.LoadFromDirectoryOrDefault(dir);
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

        ProviderQuery query = new ProviderQuery
        {
            RootPath = rootPath,
            ExcludedDirectories = ExcludedDirectoryNames
        };

        await using ProviderQueryService queryService =
            new ProviderQueryService(rootPath, ExcludedDirectoryNames, diagLog);

        foreach ((string? dir, PackageMetadata? meta) in providerPackages)
        {
            (ProviderLoader.LoadedProvider? Data,
                List<ProviderLoader.LoadedStreamProvider> Sources,
                List<ProviderLoader.LoadedSinkProvider> Sinks) package = await ProviderLoader.LoadAsync(
                dir, meta, errors, options.CancellationToken);
            ProviderLoader.LoadedProvider? loaded = package.Data;
            if (loaded is not null)
            {
                queryService.RegisterOwnedProvider(loaded);
            }
            RegisterStreamingProviders(
                package.Sources, package.Sinks, typeRegistry, queryService);
            diagLog?.Invoke($"[diag] Loading provider from {dir}: {(loaded is null ? "FAILED" : loaded.PackageName)}");
            if (loaded is null)
            {
                continue;
            }

            // Register the provider's schema types (and accessors) into the TypeRegistry so
            // trait/subtype resolution works for provider-declared types — e.g. so a
            // language-specific subtype like CSharpType is known to be a subtype of Type.
            ProviderLoader.RegisterSchema(loaded.Instance, typeRegistry);
            RegisterQueryableProviderCollections(bridge.Evaluator.GlobalEnvironment, loaded.PackageName, loaded.Schema, query, queryService);

            // Register provider functions (e.g., http.Post) as qualified callables: <package>.<fn>.
            // Resolved through CopProviderProxy.GetField (which looks up "<package>.<fn>" in the env).
            RegisterProviderFunctions(bridge, loaded.PackageName, loaded.Instance, context);
        }

        foreach (BuiltinProvider builtinProvider in s_builtinProviders)
        {
            queryService.RegisterProvider(builtinProvider.Name, builtinProvider.Instance, builtinProvider.Schema);
            // Lazy: a built-in provider (e.g. filesystem / markdown parsing) is only
            // queried when one of its collections is actually accessed. Programs that
            // never use a provider pay nothing for it.
            RegisterQueryableProviderCollections(bridge.Evaluator.GlobalEnvironment, builtinProvider.Name, builtinProvider.Schema, query, queryService);
            RegisterProviderFunctions(bridge, builtinProvider.Name, builtinProvider.Instance, context);
        }

        // Re-register provider intrinsic with query service access (same pattern as print/save/assert)
        bridge.RegisterFunction("provider", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            string providerName = await args[0].DisplayAsync(cancellationToken);

            if (args.Count > 1 && args[1] is not CopNull)
            {
                string path = await args[1].DisplayAsync(cancellationToken);
                ProviderQuery providerQuery = new ProviderQuery
                {
                    RootPath = Path.IsPathRooted(path)
                        ? path
                        : Path.GetFullPath(Path.Combine(rootPath, path)),
                    ExcludedDirectories = ExcludedDirectoryNames
                };
                // Return a queryable collection that defers provider execution until materialization.
                // Filters from predicate chains will be accumulated and pushed to the provider.
                return queryService.CreateValue(providerName, providerQuery);
            }

            return env.Lookup(providerName) is CopProviderProxy proxy ? proxy : new CopProviderProxy(providerName, env);
        });

        // Register Program with Providers list — only explicit -p providers, not import-detected ones.
        // Import-detected providers are loaded for provider('name') intrinsic access,
        // but should NOT appear in Program.Providers (the user didn't request them for analysis).
        List<CopValue> providerProxies = (topLevelProviderPackages ?? [])
            .Select(pp => PackageMetadata.LoadFromDirectoryOrDefault(pp.Dir)?.Name ??
                Path.GetFileName(pp.Dir))
            .Where(name => bridge.Evaluator.GlobalEnvironment.TryLookup(name, out _))
            .Select(name => (CopValue)new CopProviderProxy(name, bridge.Evaluator.GlobalEnvironment))
            .ToList();
        RegisterProgram(bridge, programArgs, providerProxies);

        if (errors.Count > 0)
        {
            return new EngineResult(outputs, parseErrors, errors, Warnings: warnings);
        }

        // Phase 2: Evaluate let bindings (now that provider data is available)
        leafChecks = LeafCheckCatalog.Build(
            modules.Select(m => (m.Module, m.FilePath))
                .Concat(moduleLoader.LoadedFiles.Select(f => (f.Module, f.FilePath)))
                .ToList());
        // First: deferred let bindings from imported packages
        moduleLoader.EvalDeferredLetBindings(bridge.Evaluator, errors);

        // Save original callable bindings (functions from imports) before user lets.
        // Multiple sibling modules may define `let X = X(...)` — the first module replaces
        // the function with its result, so subsequent modules need the original callable
        // restored temporarily when evaluating their RHS.
        Dictionary<string, CopValue> originalCallables = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach ((string? name, CopValue? value) in bridge.Evaluator.GlobalEnvironment.AllBindings())
        {
            if (value is ICopCallable)
            {
                originalCallables[name] = value;
            }
        }

        // Then: user module let bindings
        foreach (ParsedModule module in modules)
        {
            try
            {
                await bridge.Evaluator.EvalLetBindingsAsync(
                    module.Module, originalCallables, options.CancellationToken);
            }
            catch (CopEvaluationException ex)
            {
                errors.Add(ex.Message);
                diagLog?.Invoke($"[diag] Let initialization exception in '{module.FilePath}': {ex}");
            }
        }

        if (errors.Count > 0)
        {
            return new EngineResult(outputs, parseErrors, errors, Warnings: warnings);
        }

        // When multiple top-level packages are specified, register each package's 'command main'
        // under a package-qualified alias so all can be run in sequence.
        // Note: the parser converts `command foo` to FunctionDecl with name uppercased to "FOO".
        if (packageModuleMap is { Count: > 1 })
        {
            foreach ((string? pkgName, List<ParsedModule>? pkgModules) in packageModuleMap)
            {
                bool found = false;
                foreach (ParsedModule mod in pkgModules)
                {
                    FunctionDecl? mainFunc = mod.Module.Declarations
                        .OfType<FunctionDecl>()
                        .FirstOrDefault(fd => string.Equals(fd.Name, "MAIN", StringComparison.OrdinalIgnoreCase));
                    if (mainFunc is not null)
                    {
                        string qualifiedName = NormalizeCommandName(pkgName);
                        CopFunction cmdFunc = new CopFunction(mainFunc, bridge.Evaluator.GlobalEnvironment);
                        bridge.Evaluator.GlobalEnvironment.Define(qualifiedName, cmdFunc);
                        diagLog?.Invoke($"[diag] Registered package command '{qualifiedName}' from {pkgName}");
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    diagLog?.Invoke($"[diag] No 'command main' found in package '{pkgName}'");
                }
            }
        }

        if (IsRuleProfilingEnabled)
        {
            await ProfileAllRulesAsync(
                bridge, setupSw.ElapsedMilliseconds, options.CancellationToken);
        }

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
        foreach (string command in commandsToRun)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                diagLog?.Invoke($"[diag] Running command '{command}'");
                CopValue? result = await bridge.RunCommandAsync(command, options.CancellationToken);
                diagLog?.Invoke($"[diag] Command '{command}' returned: {result?.GetType().Name}");

                // If command returns an integer, use it as the exit code
                if (result is CopInt exitInt)
                {
                    exitCode = exitInt.Value != 0 ? exitInt.Value : exitCode ?? 0;
                }
                else
                {
                    int? routedExitCode = await TryRouteViolationsThroughCheckAsync(
                        bridge, result, options.CancellationToken);
                    if (routedExitCode is not null)
                    {
                        // A raw [Violation] result was routed through CHECK; use CHECK's count as the
                        // exit code structurally, instead of string-sniffing the formatted output.
                        exitCode = routedExitCode != 0 ? routedExitCode : exitCode ?? 0;
                    }
                    else
                    {
                        // If a command returns a collection (e.g., let-binding used as a named rule),
                        // iterate items and produce output for each.
                        await CollectCollectionOutputsAsync(
                            result, outputs, options.CancellationToken);
                    }
                }
                diagLog?.Invoke($"[diag] After collect, outputs count = {outputs.Count}");
            }
            catch (Exception ex) when (ex is CopEvaluationException or IOException or
                UnauthorizedAccessException or System.Security.SecurityException or
                HttpRequestException or TimeoutException or ArgumentException or
                InvalidOperationException or NotSupportedException or FormatException or
                System.Text.Json.JsonException)
            {
                errors.Add($"Command '{command}' failed: {ex.Message}");
                diagLog?.Invoke($"[diag] Command exception: {ex}");
            }
        }

        try { await context.AssertUnchangedAsync(options.CancellationToken); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            errors.Add($"Review snapshot: {ex.Message}");
        }

        // Check for "Command not found" errors and enhance with exported member listing
        errors.AddRange(bridge.Errors);
        List<string> notFoundErrors = errors.Where(e => e.Contains("not found") && e.Contains("Command")).ToList();
        if (notFoundErrors.Count > 0)
        {
            List<string> exportedMembers = GetExportedMembers(modules);
            if (exportedMembers.Count > 0)
            {
                foreach (string nfe in notFoundErrors)
                {
                    errors.Remove(nfe);
                }

                StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("Package has no 'command main'. Exported functions:");
                foreach (string member in exportedMembers)
                {
                    sb.AppendLine($"  {member}");
                }

                errors.Add(sb.ToString().TrimEnd());
            }
        }
        else
        {
            // No command-not-found — just add bridge errors normally
            // (already added above, so no-op for the else case)
        }

        // Collect structured diagnostics from module loader
        List<CopDiagnostic> diagnostics = new List<CopDiagnostic>();
        diagnostics.AddRange(moduleLoader.Diagnostics);

        List<FileOutput> fileOutputs = fileOutputLines
            .Select(kv => new FileOutput(kv.Key, string.Join(System.Environment.NewLine, kv.Value)))
            .ToList();

        string? resultCommandName = commandFilter is { Length: > 0 }
            ? commandsToRun.Count == 1 ? commandsToRun[0] : null
            : commandName ?? "main";

        // Upgrade bare runtime errors ("line N: message") to the same rich form `cop verify` uses
        // for parse errors — "file(N): error: message" plus the offending source line — so a runtime
        // failure reads as nicely as a syntax error.
        EnrichRuntimeErrorsWithSource(errors, modules);

        // Drain only after commands and collection output have forced deferred provider work.
        warnings.AddRange(Cop.Core.ProviderWarnings.Drain());
        IReadOnlyList<string> providerErrors = Cop.Core.ProviderErrors.Drain();
        if (providerErrors.Count > 0)
        {
            warnings.Add($"Error: {providerErrors.Count} source analysis error(s) — the analysis model may be incomplete:");
            warnings.AddRange(providerErrors);
            if (exitCode.GetValueOrDefault() == 0)
            {
                exitCode = 1;
            }
        }

        return new EngineResult(outputs, parseErrors, errors, resultCommandName, fileOutputs, warnings, asserts, diagnostics, exitCode)
        {
            Statistics = statistics.Snapshot()
        };
    }

    /// <summary>
    /// Rewrites runtime error strings of the form "line N: message" into
    /// "file(N): error: message\n  N | &lt;source line&gt;" using the program's source. Only applies
    /// when there is a single unambiguous source file; multi-file programs are left untouched.
    /// </summary>
    private static void EnrichRuntimeErrorsWithSource(List<string> errors, List<ParsedModule> modules)
    {
        if (errors.Count == 0 || modules.Count != 1)
        {
            return;
        }

        string file = modules[0].FilePath;
        if (string.IsNullOrEmpty(file))
        {
            return;
        }

        string source = modules[0].Source;
        for (int i = 0; i < errors.Count; i++)
        {
            string error = errors[i];
            int separator = error.IndexOf(": ", StringComparison.Ordinal);
            if (!error.StartsWith("line ", StringComparison.Ordinal) ||
                separator <= 5 || separator + 2 >= error.Length ||
                !int.TryParse(error.AsSpan(5, separator - 5),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int line) ||
                line < 1)
            {
                continue;
            }

            string message = error[(separator + 2)..];
            string? snippet = ParseException.GetSourceLine(source, line);
            errors[i] = snippet is not null
                ? $"{file}({line}): error: {message}\n  {line} | {snippet}"
                : $"{file}({line}): error: {message}";
        }
    }

    /// <summary>
    /// Times the force+enumerate cost of every top-level rule (let-binding that
    /// evaluates to a collection) and prints a `[profile]` breakdown to stderr.
    /// Leaf rules are forced before aggregate ("all-*") rules so the aggregates are
    /// not charged the cost of the leaves they reference (thunks memoize once forced).
    /// AI rules are skipped because forcing them would make a network call.
    /// </summary>
    private static async ValueTask ProfileAllRulesAsync(
        LanguageBridge bridge, long setupMs, CancellationToken cancellationToken)
    {
        Evaluator evaluator = bridge.Evaluator;
        Action<string>? savedTrace = evaluator.TraceLog;

        // AI rules make network calls (ai.judge) — never force them while profiling.
        HashSet<string> skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "all-ai-violations",
            "core-purity-violations",
        };

        List<(string Name, CopThunk Thunk)> thunks = evaluator.GlobalEnvironment.AllBindings()
            .Where(b => b.Value is CopThunk && !skip.Contains(b.Key))
            .Select(b => (Name: b.Key, Thunk: (CopThunk)b.Value))
            // Leaves first (rules), aggregates ("all-*") last.
            .OrderBy(t => t.Name.StartsWith("all-", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        List<(string Name, double Ms, int Count, string? Error)> results = new List<(string Name, double Ms, int Count, string? Error)>();
        Stopwatch sw = new Stopwatch();
        try
        {
            evaluator.TraceLog = null;
            foreach ((string? name, CopThunk? thunk) in thunks)
            {
                sw.Restart();
                try
                {
                    int count = await CountForcedAsync(
                        await thunk.ForceAsync(cancellationToken), cancellationToken);
                    sw.Stop();
                    results.Add((name, sw.Elapsed.TotalMilliseconds, count, null));
                }
                catch (Exception ex) when (ex is CopEvaluationException or IOException or
                    UnauthorizedAccessException or System.Security.SecurityException or
                    HttpRequestException or TimeoutException or ArgumentException or
                    InvalidOperationException or NotSupportedException or FormatException or
                    System.Text.Json.JsonException)
                {
                    sw.Stop();
                    results.Add((name, sw.Elapsed.TotalMilliseconds, -1, ex.Message));
                }
            }
        }
        finally
        {
            evaluator.TraceLog = savedTrace;
        }

        double ruleTotal = results.Sum(r => r.Ms);
        Console.Error.WriteLine();
        Console.Error.WriteLine($"[profile] setup (parse + load providers): {setupMs} ms");
        Console.Error.WriteLine($"[profile] rules total: {ruleTotal:F1} ms across {results.Count} rules");
        Console.Error.WriteLine($"[profile] {"ms",8}  {"items",6}  rule");
        foreach ((string Name, double Ms, int Count, string? Error) r in results.OrderByDescending(r => r.Ms).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            string items = r.Count < 0 ? "ERR" : r.Count.ToString();
            string err = r.Error is null ? "" : "  -- " + r.Error;
            Console.Error.WriteLine($"[profile] {r.Ms,8:F2}  {items,6}  {r.Name}{err}");
        }
        Console.Error.WriteLine();
    }

    /// <summary>Forces and counts the items of a (possibly lazy) collection value.</summary>
    private static async ValueTask<int> CountForcedAsync(
        CopValue v, CancellationToken cancellationToken)
    {
        while (v is CopThunk t)
        {
            v = await t.ForceAsync(cancellationToken);
        }

        return v switch
        {
            CopList l => l.Items.Count,
            CopLazyCollection lz => await lz.EnumerateAsync(cancellationToken)
                .CountAsync(cancellationToken),
            CopQueryable q => await q.EnumerateAsync(cancellationToken)
                .CountAsync(cancellationToken),
            _ => 0,
        };
    }

    private static LanguageBridge CreateBridge(
        List<PrintOutput> outputs,
        Dictionary<string, List<string>> fileOutputLines,
        List<AssertResult> asserts,
        Action<string>? diagLog)
    {
        ForeignFunctionRegistry ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        ffi.RegisterAsync("print", async (args, env, cancellationToken) =>
        {
            string text = args.Count > 0
                ? await args[0].DisplayAsync(cancellationToken) : string.Empty;
            outputs.Add(CreatePrintOutput(text));
            return CopNull.Instance;
        });

        ffi.RegisterAsync("debug", async (args, env, cancellationToken) =>
        {
            string text = args.Count > 0
                ? await args[0].DisplayAsync(cancellationToken) : string.Empty;
            diagLog?.Invoke($"[debug] {text}");
            return CopNull.Instance;
        });

        ffi.RegisterAsync("save", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopNull.Instance;
            }

            string path = await args[0].DisplayAsync(cancellationToken);
            string content = await args[1].DisplayAsync(cancellationToken);
            if (!fileOutputLines.TryGetValue(path, out List<string>? lines))
            {
                lines = [];
                fileOutputLines[path] = lines;
            }

            lines.Add(content);
            return CopNull.Instance;
        });

        ffi.RegisterAsync("assert", async (args, env, cancellationToken) =>
        {
            bool passed = args.Count == 0 ||
                await args[0].IsTruthyAsync(cancellationToken);
            string description = args.Count > 1
                ? await args[1].DisplayAsync(cancellationToken) : "assert";
            asserts.Add(new AssertResult(description, passed, passed ? string.Empty : $"assert failed: {description}", 0));
            return CopNull.Instance;
        });

        return new LanguageBridge(ffi);
    }

    // Marshals only the values passed to a provider, preserving lazy CLR semantic fields.
    private static void RegisterProviderFunctions(LanguageBridge bridge, string name, DataProvider provider, AnalysisContext context)
    {
        Dictionary<string, Func<List<object?>, CancellationToken, ValueTask<object?>>>?
            functions = provider.GetFunctions(context);
        if (functions is null)
        {
            return;
        }

        foreach ((string functionName, Func<List<object?>, CancellationToken,
            ValueTask<object?>> function) in functions)
        {
            string qualifiedName = $"{name}.{functionName}";
            bridge.Evaluator.GlobalEnvironment.Define(qualifiedName,
                new CopExternalFunction(qualifiedName, (args, _, cancellationToken) =>
                    InvokeProviderFunctionAsync(function, args, cancellationToken)));
        }
    }

    private static async ValueTask<CopValue> InvokeProviderFunctionAsync(
        Func<List<object?>, CancellationToken, ValueTask<object?>> function,
        IReadOnlyList<CopValue> args, CancellationToken cancellationToken)
    {
        List<object?> clrArgs = new(args.Count);
        foreach (CopValue argument in args)
        {
            clrArgs.Add(await CopToClrAsync(argument, cancellationToken));
        }
        object? result = await function(clrArgs, cancellationToken);
        return DataObjectAdapter.Marshal(result);
    }

    // Converts a CopValue to a plain CLR value for passing into a provider function.
    private static async ValueTask<object?> CopToClrAsync(
        CopValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (value is CopThunk thunk)
        {
            value = await thunk.ForceAsync(cancellationToken);
        }

        return value switch
        {
            CopNull => null,
            CopString s => s.Value,
            CopInt i => i.Value,
            CopNumber n => n.Value,
            CopBool b => b.IsTrue,
            CopList l => await CopCollectionToClrAsync(
                l.Items.ToAsyncEnumerable(), cancellationToken),
            CopLazyCollection lazy => await CopCollectionToClrAsync(
                lazy.EnumerateAsync(cancellationToken), cancellationToken),
            CopQueryable q => await CopCollectionToClrAsync(
                q.EnumerateAsync(cancellationToken), cancellationToken),
            CopProviderProxy { Data: { } data } =>
                await CopObjectToDataObjectAsync(data, cancellationToken),
            CopDynamicObject d => d.Underlying,
            CopObject o => await CopObjectToDataObjectAsync(o, cancellationToken),
            _ => await value.DisplayAsync(cancellationToken)
        };
    }

    private static async ValueTask<List<object?>> CopCollectionToClrAsync(
        IAsyncEnumerable<CopValue> values, CancellationToken cancellationToken)
    {
        List<object?> items = [];
        await foreach (CopValue value in values.WithCancellation(cancellationToken))
        {
            items.Add(await CopToClrAsync(value, cancellationToken));
        }
        return items;
    }

    private static async ValueTask<DataObject> CopObjectToDataObjectAsync(
        CopObject obj, CancellationToken cancellationToken)
    {
        DataObject data = new DataObject(obj.TypeName ?? "Object");
        foreach ((string? key, CopValue? val) in obj.Fields)
        {
            data.Set(key, await CopToClrAsync(val, cancellationToken));
        }

        return data;
    }

    private static void RegisterProgram(LanguageBridge bridge, string[]? programArgs, List<CopValue>? providers = null)
    {
        List<CopValue> args = (programArgs ?? [])
            .Select(arg => (CopValue)new CopString(arg))
            .ToList();
        CopObject programObj = new CopObject(new Dictionary<string, CopValue>(StringComparer.Ordinal)
        {
            ["Args"] = new CopList(args),
            ["Providers"] = new CopList(providers ?? [])
        })
        { TypeName = "Program" };

        // Register as both a value (Program) and FFI function (program())
        bridge.RegisterValue("Program", programObj);
        bridge.RegisterFunction("program", (_, _, _) =>
            ValueTask.FromResult<CopValue>(programObj), 0);
    }

    /// <summary>
    /// Registers lazy collections for a provider. Data is not loaded until a collection
    /// is first accessed, avoiding expensive provider queries (e.g., Roslyn parsing)
    /// for unused providers.
    /// </summary>
    private static void RegisterQueryableProviderCollections(
        Cop.Lang.Interpreter.Environment env, string providerName,
        ProviderSchema schema, ProviderQuery query, ProviderQueryService service)
    {
        CopValue value = service.CreateValue(providerName, query);
        if (value is CopQueryable collection && schema.Collections.Count == 1)
        {
            env.Define($"{providerName}.{schema.Collections[0].Name}", collection);
        }

        if (value is CopObject obj)
        {
            foreach ((string? name, CopValue? field) in obj.Fields)
            {
                env.Define($"{providerName}.{name}", field);
            }
        }

        env.Define(providerName, new CopProviderProxy(providerName, env, value as CopObject));
    }

    /// <summary>
    /// When a named rule (e.g. `-c &lt;name&gt;` resolving to a `let` binding) evaluates to a
    /// collection of Violations, route it through the `CHECK` command so it produces the same
    /// formatted `file(line): level: message` output — and the same non-zero exit — as
    /// `CHECK(&lt;name&gt;)`, instead of dumping raw Violation objects with a clean exit (issue #52).
    /// CHECK prints each violation via the `print` FFI, which appends to the engine's outputs
    /// list, so both the formatting and the violation exit code follow automatically.
    /// Returns the exit code (CHECK's violation count) when it routed the result, or null when it
    /// did not (caller should fall back to the raw-dump path).
    /// </summary>
    private static async ValueTask<int?> TryRouteViolationsThroughCheckAsync(
        LanguageBridge bridge, CopValue? result, CancellationToken cancellationToken)
    {
        if (result is null)
        {
            return null;
        }

        if (result is CopThunk thunk)
        {
            result = await thunk.ForceAsync(cancellationToken);
        }

        List<CopValue>? items = result switch
        {
            CopList list => list.Items.ToList(),
            CopLazyCollection lazy => await lazy.EnumerateAsync(cancellationToken)
                .ToListAsync(cancellationToken),
            CopQueryable q => await q.EnumerateAsync(cancellationToken)
                .ToListAsync(cancellationToken),
            _ => null
        };

        // Only route non-empty collections whose every item is a Violation. Empty lists fall
        // through — they produce no output and a clean exit either way.
        if (items is null || items.Count == 0)
        {
            return null;
        }
        for (int i = 0; i < items.Count; i++)
        {
            while (items[i] is CopThunk item)
            {
                items[i] = await item.ForceAsync(cancellationToken);
            }
            if (!IsViolation(items[i]))
            {
                return null;
            }
        }

        Lang.Interpreter.Environment env = bridge.Evaluator.GlobalEnvironment;
        if (env.TryLookup(CheckCommandName, out CopValue? check) && check is ICopCallable callable)
        {
            CopValue checkResult = await callable.InvokeAsync(
                [new CopList(items)], bridge.Evaluator, env, cancellationToken);
            if (checkResult is CopThunk t)
            {
                checkResult = await t.ForceAsync(cancellationToken);
            }
            // CHECK returns the violation count; fall back to the item count if it returns anything
            // else, so a non-empty violation list always yields a non-zero (failing) exit code.
            return checkResult is CopInt count ? count.Value : items.Count;
        }

        return null;
    }

    /// <summary>
    /// The package-defined command the engine routes a <c>[Violation]</c> result through so it is
    /// formatted and exit-coded consistently. This is the engine's one (narrow, documented) coupling
    /// to the violation/check domain; see <see cref="TryRouteViolationsThroughCheckAsync"/>.
    /// </summary>
    private const string CheckCommandName = "CHECK";

    private static bool IsViolation(CopValue value)
    {
        return value switch
        {
            CopObject obj => string.Equals(obj.TypeName, "Violation", StringComparison.Ordinal),
            CopDynamicObject dyn => string.Equals(dyn.TypeName, "Violation", StringComparison.Ordinal),
            _ => false
        };
    }

    private static async ValueTask CollectCollectionOutputsAsync(
        CopValue? result, List<PrintOutput> outputs, CancellationToken cancellationToken)
    {
        while (result is CopThunk thunk)
        {
            result = await thunk.ForceAsync(cancellationToken);
        }
        IAsyncEnumerable<CopValue>? items = result switch
        {
            CopList list => list.Items.ToAsyncEnumerable(),
            CopLazyCollection lazy => lazy.EnumerateAsync(cancellationToken),
            CopQueryable query => query.EnumerateAsync(cancellationToken),
            _ => null
        };
        if (items is null)
        {
            return;
        }
        await foreach (CopValue item in items.WithCancellation(cancellationToken))
        {
            string text = await item.DisplayAsync(cancellationToken);
            if (!string.IsNullOrEmpty(text))
            {
                outputs.Add(CreatePrintOutput(text));
            }
        }
    }

    private static string NormalizeCommandName(string name)
        => string.IsNullOrWhiteSpace(name) ? name : name.ToUpperInvariant();

    private static List<string> GetExportedMembers(List<ParsedModule> modules)
    {
        List<string> members = new List<string>();
        foreach (ParsedModule mod in modules)
        {
            foreach (Declaration decl in mod.Module.Declarations)
            {
                if (decl is FunctionDecl fd && fd.IsExported && !string.Equals(fd.Name, "MAIN", StringComparison.OrdinalIgnoreCase))
                {
                    string paramsStr = string.Join(", ", fd.Params.Select(p =>
                        p.Type is not null ? $"{p.Name} : {FormatTypeRef(p.Type)}" : p.Name));
                    string returnStr = fd.ReturnType is not null ? $" : {FormatTypeRef(fd.ReturnType)}" : "";
                    members.Add($"{fd.Name}({paramsStr}){returnStr}");
                }
                else if (decl is LetDecl ld && ld.IsExported)
                {
                    string typeStr = ld.TypeAnnotation is not null ? $" : {FormatTypeRef(ld.TypeAnnotation)}" : "";
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
        {
            return new PrintOutput(text);
        }

        List<TextSpan> spans = new List<TextSpan>();
        int position = 0;
        while (position < text.Length)
        {
            int open = text.IndexOf('{', position);
            if (open < 0)
            {
                if (position < text.Length)
                {
                    spans.Add(new TextSpan(text[position..]));
                }

                break;
            }

            if (open > position)
            {
                spans.Add(new TextSpan(text[position..open]));
            }

            int at = text.IndexOf('@', open + 1);
            int close = text.IndexOf('}', open + 1);
            if (at < 0 || close < 0 || at > close)
            {
                spans.Add(new TextSpan(text[open].ToString()));
                position = open + 1;
                continue;
            }

            string content = text[(open + 1)..at];
            string annotation = text[(at + 1)..close];
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
    public static async Task<EngineResult> RunProjectAsync(
        List<string> feedPaths,
        List<string> packageNames,
        string rootPath,
        List<string> rules,
        EngineRunOptions? options = null)
    {
        options ??= new EngineRunOptions();
        Action<string>? diagLog = options.DiagnosticLog;
        string[]? additionalScriptFiles = options.ScriptFiles?.ToArray();
        string[]? providers = options.ProviderNames?.ToArray();
        options.CancellationToken.ThrowIfCancellationRequested();
        rootPath = Path.GetFullPath(rootPath);

        List<string> parseErrors = new List<string>();
        List<string> fatalErrors = new List<string>();
        List<ParsedModule> modules = new List<ParsedModule>();
        List<(string Dir, PackageMetadata Meta)> providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        Dictionary<string, List<ParsedModule>> packageModuleMap = new Dictionary<string, List<ParsedModule>>(StringComparer.OrdinalIgnoreCase);
        List<string> normalizedFeedPaths = feedPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string packageName in packageNames)
        {
            bool found = false;
            foreach (string feedPath in normalizedFeedPaths)
            {
                string? packageDir = ImportResolver.FindPackageDir(feedPath, packageName);
                if (packageDir is null)
                {
                    continue;
                }

                string srcDir = Path.Combine(packageDir, "src");
                if (Directory.Exists(srcDir))
                {
                    string[] copFiles = Directory.GetFiles(srcDir, "*.cop");
                    Array.Sort(copFiles, StringComparer.Ordinal);
                    List<ParsedModule> parsed = ParseModules(copFiles, parseErrors);
                    modules.AddRange(parsed);
                    packageModuleMap[packageName] = parsed;
                }

                DetectProviderPackage(Path.Combine(packageDir, "src"), providerPackages);
                found = true;
                break;
            }

            if (!found)
            {
                fatalErrors.Add($"Package '{packageName}' not found in any feed");
            }
        }

        // Include additional script files (e.g., user-global checks)
        if (additionalScriptFiles is { Length: > 0 })
        {
            modules.AddRange(ParseModules(additionalScriptFiles, parseErrors));
        }

        // Resolve -p provider packages
        if (providers is { Length: > 0 })
        {
            foreach (string providerName in providers)
            {
                string? pkgDir = null;
                foreach (string feedPath in normalizedFeedPaths)
                {
                    pkgDir = ImportResolver.FindPackageDir(feedPath, providerName);
                    if (pkgDir != null)
                    {
                        break;
                    }
                }
                if (pkgDir is null)
                {
                    fatalErrors.Add($"Provider package '{providerName}' not found in any feed");
                    continue;
                }
                PackageMetadata? metadata = PackageMetadata.LoadFromDirectoryOrDefault(pkgDir);
                if (metadata is not null && metadata.IsProvider)
                {
                    providerPackages.Add((pkgDir, metadata));
                }
                else
                {
                    fatalErrors.Add($"Package '{providerName}' is not a provider (no lib/ with DLL)");
                }
            }
        }

        if (fatalErrors.Count > 0)
        {
            return new EngineResult([], parseErrors, fatalErrors);
        }

        if (modules.Count == 0)
        {
            return new EngineResult([], parseErrors, ["No .cop files found in packages"]);
        }

        return await ExecuteModulesAsync(
            modules,
            normalizedFeedPaths,
            rootPath,
            parseErrors,
            options with
            {
                CommandName = rules.Count == 0 ? "main" : null,
                CommandFilter = rules.Count > 0 ? rules : null,
                ProviderPackages = providerPackages,
                PackageModules = packageModuleMap.Count > 1 ? packageModuleMap : null
            });
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

    private static void DetectProviderPackage(
        string copDirPath, List<(string Dir, PackageMetadata Meta)> providerPackages)
    {
        // copDirPath is the package's src/ or types/ directory (from ImportResolver).
        // The package root is its parent directory.
        string? packageDir = Path.GetDirectoryName(copDirPath);
        if (packageDir is null)
        {
            return;
        }

        PackageMetadata? metadata = PackageMetadata.LoadFromDirectoryOrDefault(packageDir);
        if (metadata is null)
        {
            return;
        }

        if (metadata.IsProvider)
        {
            providerPackages.Add((packageDir, metadata));
        }
    }

    /// <summary>
    /// Loads external CLR providers: registers their schemas into the type registry
    /// and queries them for collection data.
    /// </summary>
    private static async ValueTask LoadExternalProvidersAsync(
        ReplContext context, List<string> errors, List<string> fatalErrors,
        CancellationToken cancellationToken)
    {
        foreach ((string dir, PackageMetadata meta) in context.ProviderPackages)
        {
            (ProviderLoader.LoadedProvider? Data,
                List<ProviderLoader.LoadedStreamProvider> Sources,
                List<ProviderLoader.LoadedSinkProvider> Sinks) package = await ProviderLoader.LoadAsync(
                dir, meta, fatalErrors, cancellationToken);
            ProviderLoader.LoadedProvider? loaded = package.Data;
            if (loaded is not null)
            {
                context.QueryService.RegisterOwnedProvider(loaded);
            }
            RegisterStreamingProviders(package.Sources, package.Sinks,
                context.TypeRegistry, context.QueryService);
            if (loaded is null)
            {
                continue;
            }

            // Register schema, types, accessors, and bindings
            ProviderLoader.RegisterSchema(loaded.Instance, context.TypeRegistry);

            // Query for data and register global collections
            await ProviderLoader.QueryAndRegisterAsync(loaded, context.TypeRegistry,
                new ProviderQuery
                {
                    RootPath = context.RootPath,
                    ExcludedDirectories = ExcludedDirectoryNames
                }, errors, cancellationToken);

            // Initialize capabilities (document loaders, file parsers, etc.)
            ProviderLoader.InitializeCapabilities(
                loaded.Instance, context.TypeRegistry, context.RootPath);

            // Register with query service for path-scoped queries
        }
    }

    /// <summary>
    /// Registers streaming capabilities and transfers all source and sink lifetimes.
    /// </summary>
    private static void RegisterStreamingProviders(
        List<ProviderLoader.LoadedStreamProvider> sources,
        List<ProviderLoader.LoadedSinkProvider> sinks,
        TypeRegistry typeRegistry, ProviderQueryService queryService)
    {
        foreach (ProviderLoader.LoadedStreamProvider source in sources)
        {
            queryService.RegisterOwnedResource(source.Instance);
        }
        foreach (ProviderLoader.LoadedSinkProvider sink in sinks)
        {
            queryService.RegisterOwnedResource(sink.Instance);
        }
        foreach (ProviderLoader.LoadedStreamProvider source in sources)
        {
            ProviderSchema schema = ProviderLoader.RegisterSchema(source.Instance, typeRegistry);
            ProviderLoader.RegisterStreamProvider(
                source.Instance, schema, source.PackageName, typeRegistry);
        }
        foreach (ProviderLoader.LoadedSinkProvider sink in sinks)
        {
            ProviderLoader.RegisterSinkProvider(sink.Instance, sink.PackageName, typeRegistry);
        }
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
        string[] scriptFilePaths = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
        Array.Sort(scriptFilePaths, StringComparer.Ordinal);
        List<ParsedModule> modules = new List<ParsedModule>();
        List<string> parseErrors = new List<string>();

        foreach (string path in scriptFilePaths)
        {
            try
            {
                string source = File.ReadAllText(path);
                modules.Add(new ParsedModule(path, source, Cop.Lang.Parser.CopParser.Parse(source, path)));
            }
            catch (ParseException ex)
            {
                if (!IsValueOnlyFile(path))
                {
                    parseErrors.Add(ex.Message);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                if (!IsValueOnlyFile(path))
                {
                    parseErrors.Add($"Error parsing {path}: {ex.Message}");
                }
            }
        }

        errors.AddRange(parseErrors);

        // Build type registry from modules using the new pipeline
        TypeRegistry typeRegistry = new TypeRegistry();
        foreach (BuiltinProvider bp in s_builtinProviders)
        {
            ProviderLoader.RegisterSchema(bp.Instance, typeRegistry);
        }

        typeRegistry.RegisterProgramType();

        // Resolve imports and register types
        List<string> feedPaths = FindFeedPaths(scriptsDir);
        string globalCachePath = Cop.Cli.Commands.PackageResolver.GlobalCachePath;
        if (Directory.Exists(globalCachePath) && !feedPaths.Contains(globalCachePath))
        {
            feedPaths.Add(globalCachePath);
        }

        ModuleLoader moduleLoader = new Cop.Lang.Interpreter.ModuleLoader(feedPaths);
        LanguageBridge bridge = CreateBridge([], [], [], null);
        foreach (ParsedModule module in modules)
        {
            try { bridge.Evaluator.RegisterDeclarations(module.Module); }
            catch (CopEvaluationException ex) { errors.Add(ex.Message); }
        }
        try
        {
            ResolveImportsRetainingCallerFunctions(
                moduleLoader, modules.Select(m => m.Module), bridge.Evaluator);
        }
        catch (CopEvaluationException ex) { errors.Add(ex.Message); }
        errors.AddRange(moduleLoader.Errors);

        // Detect provider packages
        List<(string Dir, PackageMetadata Meta)> providerPackages = new List<(string Dir, PackageMetadata Meta)>();
        foreach ((string? dir, string _) in moduleLoader.ProviderPackages)
        {
            PackageMetadata? metadata = PackageMetadata.LoadFromDirectoryOrDefault(dir);
            if (metadata is not null && metadata.IsProvider)
            {
                providerPackages.Add((dir, metadata));
            }
        }

        // Load type definitions into TypeRegistry for completion
        foreach (ParsedModule module in modules)
        {
            foreach (Declaration decl in module.Module.Declarations)
            {
                if (decl is Cop.Lang.Ast.TypeDecl typeDecl)
                {
                    List<PropertyDefinition> props = typeDecl.Properties.Select(p => new PropertyDefinition(p.Name, p.Type.Name, p.IsOptional, p.Type.IsCollection, p.Line, p.ComputedExpr)).ToList();
                    typeRegistry.LoadTypeDefinitions([new TypeDefinition(typeDecl.Name, typeDecl.BaseType, props, 0, typeDecl.IsExported, typeDecl.DocComment, typeDecl.Traits)]);
                }
            }
        }

        return new ReplContext(modules, typeRegistry, rootPath, scriptsDir, providerPackages)
        {
            TotalFileCount = scriptFilePaths.Length
        };
    }

    /// <summary>
    /// Returns true if a .cop file contains only value expressions (list literals, strings, numbers)
    /// that are valid REPL content but not valid top-level cop statements.
    /// </summary>
    private static bool IsValueOnlyFile(string path)
    {
        try
        {
            string? firstLine = File.ReadLines(path).FirstOrDefault()?.TrimStart();
            if (firstLine is null)
            {
                return false;
            }

            return firstLine.StartsWith('[') || firstLine.StartsWith('\'') ||
                   (firstLine.Length > 0 && (char.IsDigit(firstLine[0]) ||
                    (firstLine[0] == '-' && firstLine.Length > 1 && char.IsDigit(firstLine[1]))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Could not inspect REPL input '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Loads provider data into the REPL context (lazy loading on first reference).
    /// </summary>
    public static async ValueTask LoadProvidersAsync(
        ReplContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.HasLoadedProviders)
        {
            return;
        }

        List<string> errors = new List<string>();
        List<string> fatalErrors = new List<string>();

        // Load external providers
        await LoadExternalProvidersAsync(context, errors, fatalErrors, cancellationToken);
        if (fatalErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(System.Environment.NewLine, fatalErrors));
        }

        // Query all built-in providers
        foreach (BuiltinProvider bp in s_builtinProviders)
        {
            ProviderQuery query = new ProviderQuery
            {
                RootPath = context.RootPath,
                ExcludedDirectories = ExcludedDirectoryNames
            };
            await ProviderLoader.QueryAndRegisterAsync(
                new ProviderLoader.LoadedProvider(bp.Instance, bp.Schema, bp.Name),
                context.TypeRegistry, query, cancellationToken: cancellationToken);
        }

        // Initialize capabilities
        foreach (BuiltinProvider bp in s_builtinProviders)
        {
            ProviderLoader.InitializeCapabilities(bp.Instance, context.TypeRegistry, context.RootPath);
        }

        // Register built-in providers with query service for path-scoped queries
        foreach (BuiltinProvider bp in s_builtinProviders)
        {
            context.QueryService.RegisterProvider(bp.Name, bp.Instance, bp.Schema);
        }

        context.HasLoadedProviders = true;

        if (errors.Count > 0)
        {
            context.Warnings.AddRange(errors);
        }
    }
}
