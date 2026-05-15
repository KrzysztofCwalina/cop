using System.Collections;
using System.Diagnostics;
using Cop.Core;

namespace Cop.Lang;

public class ScriptInterpreter
{
    private readonly TypeRegistry _typeRegistry;
    private readonly int _maxOutputsPerCommand;
    private readonly TimeSpan _timeout;
    private Dictionary<string, IList>? _globalResolvedSelects;
    private ProgramInfo? _program;

    // Per-document cache for resolved let bindings — shared across all commands
    private readonly Dictionary<string, Dictionary<string, IList>> _documentLetCache = new();
    // Fingerprint-based cache for resolved filtered collections (order-independent).
    // Bounded to prevent unbounded memory growth on large repos with many unique queries.
    private readonly BoundedCache<string, List<object>> _queryCache = new(capacity: 2048);

    // Optional provider query service for path-scoped collection references
    private readonly IProviderQueryService? _providerQueryService;

    /// <summary>
    /// Extracts a collection name from a union element expression.
    /// Supports IdentifierExpr ("Types") and MemberAccessExpr ("csharp.Types").
    /// </summary>
    private static string GetUnionElementName(Expression expr) => expr switch
    {
        IdentifierExpr id => id.Name,
        MemberAccessExpr { Target: IdentifierExpr parent } ma => $"{parent.Name}.{ma.Member}",
        _ => throw new InvalidOperationException(
            $"Unsupported union element: expected a collection name like 'Types' or 'csharp.Types', got {expr.GetType().Name}")
    };

    private readonly Action<string>? _diagLog;

    // Sinks for intrinsic functions — set per-Run, used by CreateEvaluator
    private Action<string>? _printSink;
    private Action<string, string>? _saveSink;
    private Action<string>? _debugSink;
    private Action<bool, string>? _assertSink;

    public ScriptInterpreter(
        TypeRegistry typeRegistry,
        int maxOutputsPerCommand = 1000,
        TimeSpan? timeout = null,
        Action<string>? diagLog = null,
        IProviderQueryService? providerQueryService = null)
    {
        _typeRegistry = typeRegistry;
        _maxOutputsPerCommand = maxOutputsPerCommand;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _diagLog = diagLog;
        _providerQueryService = providerQueryService;
    }

    private PredicateEvaluator CreateEvaluator(
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        string filePath,
        Dictionary<string, LetDeclaration>? letDeclarations = null,
        Dictionary<string, List<FunctionDefinition>>? functionGroups = null,
        Dictionary<string, IList>? resolvedCollections = null)
    {
        var evaluator = new PredicateEvaluator(predicateGroups, filePath, _typeRegistry,
            letDeclarations, functionGroups, resolvedCollections, _providerQueryService,
            packagePredicates: _packagePredicates, packageFunctions: _packageFunctions, packageLets: _packageLets,
            printSink: _printSink, saveSink: _saveSink, debugSink: _debugSink, assertSink: _assertSink);
        if (_program is not null) evaluator.SetProgram(_program);
        return evaluator;
    }

    // Package-qualified symbol stores for disambiguation (populated by BuildSymbolTables)
    private Dictionary<string, Dictionary<string, List<PredicateDefinition>>>? _packagePredicates;
    private Dictionary<string, Dictionary<string, List<FunctionDefinition>>>? _packageFunctions;
    private Dictionary<string, Dictionary<string, LetDeclaration>>? _packageLets;

    /// <summary>
    /// Builds predicate groups, function groups, and let declarations from script files.
    /// Detects name conflicts: local-local duplicates are errors, import-import duplicates
    /// require qualified access, local-import conflicts give local precedence with a warning.
    /// Also populates package-qualified symbol stores for disambiguation.
    /// </summary>
    private (Dictionary<string, List<PredicateDefinition>> predicateGroups,
             Dictionary<string, List<FunctionDefinition>> functionGroups,
             Dictionary<string, LetDeclaration> letDeclarations)
        BuildSymbolTables(List<ScriptFile> scriptFiles, List<string> errors)
    {
        var predicateGroups = new Dictionary<string, List<PredicateDefinition>>();
        var functionGroups = new Dictionary<string, List<FunctionDefinition>>();
        var letDeclarations = new Dictionary<string, LetDeclaration>();

        // Package-qualified stores: packageName → (symbolName → definition(s))
        var pkgPredicates = new Dictionary<string, Dictionary<string, List<PredicateDefinition>>>();
        var pkgFunctions = new Dictionary<string, Dictionary<string, List<FunctionDefinition>>>();
        var pkgLets = new Dictionary<string, Dictionary<string, LetDeclaration>>();

        foreach (var sf in scriptFiles)
        {
            // Register predicates with conflict detection
            foreach (var pred in sf.Predicates)
            {
                if (!predicateGroups.TryGetValue(pred.Name, out var group))
                {
                    group = [];
                    predicateGroups[pred.Name] = group;
                }

                // Check for same-name, same-type conflicts
                foreach (var existing in group)
                {
                    if (!string.Equals(existing.ParameterType, pred.ParameterType, StringComparison.Ordinal))
                        continue; // different input types = valid overload

                    if (existing.PackageName is null && pred.PackageName is null)
                    {
                        // Local-local duplicate with same input type
                        errors.Add($"Duplicate predicate '{pred.Name}({pred.ParameterType})' defined in multiple local files");
                        break;
                    }
                    if (existing.PackageName is not null && pred.PackageName is not null
                        && existing.PackageName != pred.PackageName)
                    {
                        // Import-import conflict — both stay, user must qualify
                        errors.Add($"Ambiguous predicate '{pred.Name}({pred.ParameterType})' defined in packages '{existing.PackageName}' and '{pred.PackageName}'. Use '{existing.PackageName}.{pred.Name}' or '{pred.PackageName}.{pred.Name}' to disambiguate.");
                        break;
                    }
                }

                group.Add(pred);

                // Track in package-qualified store
                if (pred.PackageName is not null)
                {
                    if (!pkgPredicates.TryGetValue(pred.PackageName, out var pkgGroup))
                    {
                        pkgGroup = new Dictionary<string, List<PredicateDefinition>>();
                        pkgPredicates[pred.PackageName] = pkgGroup;
                    }
                    if (!pkgGroup.TryGetValue(pred.Name, out var pkgNameGroup))
                    {
                        pkgNameGroup = [];
                        pkgGroup[pred.Name] = pkgNameGroup;
                    }
                    pkgNameGroup.Add(pred);
                }
            }

            // Register functions with conflict detection
            foreach (var func in sf.Functions)
            {
                // Skip Collection-typed intrinsics — they are declaration-only (for docs/discovery).
                // Collection methods are handled by the collection method dispatcher in PredicateEvaluator.
                if (func.IsIntrinsic && string.Equals(func.InputType, "Collection", StringComparison.Ordinal))
                    continue;

                if (!functionGroups.TryGetValue(func.Name, out var group))
                {
                    group = [];
                    functionGroups[func.Name] = group;
                }

                // Check for same-name, same-type conflicts
                foreach (var existing in group)
                {
                    if (!string.Equals(existing.InputType, func.InputType, StringComparison.Ordinal))
                        continue; // different input types = valid overload

                    if (existing.PackageName is null && func.PackageName is null)
                    {
                        errors.Add($"Duplicate function '{func.Name}({func.InputType})' defined in multiple local files");
                        break;
                    }
                    if (existing.PackageName is not null && func.PackageName is not null
                        && existing.PackageName != func.PackageName)
                    {
                        errors.Add($"Ambiguous function '{func.Name}({func.InputType})' defined in packages '{existing.PackageName}' and '{func.PackageName}'. Use '{existing.PackageName}.{func.Name}' or '{func.PackageName}.{func.Name}' to disambiguate.");
                        break;
                    }
                }

                group.Add(func);

                // Track in package-qualified store
                if (func.PackageName is not null)
                {
                    if (!pkgFunctions.TryGetValue(func.PackageName, out var pkgGroup))
                    {
                        pkgGroup = new Dictionary<string, List<FunctionDefinition>>();
                        pkgFunctions[func.PackageName] = pkgGroup;
                    }
                    if (!pkgGroup.TryGetValue(func.Name, out var pkgNameGroup))
                    {
                        pkgNameGroup = [];
                        pkgGroup[func.Name] = pkgNameGroup;
                    }
                    pkgNameGroup.Add(func);
                }
            }

            // Register let declarations with conflict detection
            foreach (var let in sf.LetDeclarations)
            {
                if (letDeclarations.TryGetValue(let.Name, out var existing))
                {
                    if (existing.PackageName is null && let.PackageName is null)
                    {
                        errors.Add($"Duplicate let binding '{let.Name}' defined in multiple local files");
                    }
                    else if (existing.PackageName is null && let.PackageName is not null)
                    {
                        // Local already registered, imported version comes later — local wins, skip
                        // Track in package store for qualified access
                    }
                    else if (existing.PackageName is not null && let.PackageName is null)
                    {
                        // Local overrides import — replace
                        letDeclarations[let.Name] = let;
                    }
                    else if (existing.PackageName is not null && let.PackageName is not null
                             && existing.PackageName != let.PackageName)
                    {
                        errors.Add($"Ambiguous let binding '{let.Name}' defined in packages '{existing.PackageName}' and '{let.PackageName}'. Use '{existing.PackageName}.{let.Name}' or '{let.PackageName}.{let.Name}' to disambiguate.");
                    }
                    // else: same package redefinition — last one wins (within same package)
                }
                else
                {
                    letDeclarations[let.Name] = let;
                }

                // Track in package-qualified store
                if (let.PackageName is not null)
                {
                    if (!pkgLets.TryGetValue(let.PackageName, out var pkgLetMap))
                    {
                        pkgLetMap = new Dictionary<string, LetDeclaration>();
                        pkgLets[let.PackageName] = pkgLetMap;
                    }
                    pkgLetMap[let.Name] = let;
                }
            }
        }

        _packagePredicates = pkgPredicates;
        _packageFunctions = pkgFunctions;
        _packageLets = pkgLets;

        return (predicateGroups, functionGroups, letDeclarations);
    }

    public InterpreterResult Run(
        List<ScriptFile> scriptFiles,
        List<Document> documents,
        string? commandName = null,
        string[]? programArgs = null,
        HashSet<string>? commandFilter = null,
        bool assertMode = false)
    {
        var allOutputs = new List<PrintOutput>();
        var fileOutputs = new Dictionary<string, List<string>>();
        var allAsserts = new List<AssertResult>();

        // Wire up intrinsic function sinks for this run
        _printSink = message => allOutputs.Add(new PrintOutput(new RichString(new[] { new TextSpan(message) })));
        _saveSink = (path, content) =>
        {
            if (!fileOutputs.TryGetValue(path, out var lines)) { lines = []; fileOutputs[path] = lines; }
            lines.Add(content);
        };
        _debugSink = _diagLog is not null ? message => _diagLog($"[debug] {message}") : null;
        _assertSink = (condition, description) => allAsserts.Add(new AssertResult(description, condition, condition ? "" : $"assert failed: {description}", 0));

        // Create Program built-in
        var program = new ProgramInfo(new List<string>(programArgs ?? []));
        _program = program;

        // Build symbol tables with conflict detection
        var errors = new List<string>();
        var (predicateGroups, functionGroups, letDeclarations) = BuildSymbolTables(scriptFiles, errors);
        // Report symbol conflicts as interpreter errors (non-fatal — continue with best-effort resolution)

        // Compute aggregate collection counts
        var aggregateCounts = ComputeAggregateCounts(documents);

        // Pre-resolve let declarations that use .Select() — these need global (cross-document) data
        _globalResolvedSelects = PreResolveGlobalSelects(
            letDeclarations, documents, predicateGroups, functionGroups);

        // Build command lookup table across all script files for expanding refs
        var allCommands = new Dictionary<string, List<CommandBlock>>(StringComparer.Ordinal);
        foreach (var cf in scriptFiles)
        {
            foreach (var c in cf.Commands)
            {
                if (!c.IsCommand || c.CommandRef != null) continue;
                if (!allCommands.TryGetValue(c.Name, out var list))
                {
                    list = [];
                    allCommands[c.Name] = list;
                }
                list.Add(c);
            }
        }

        // Run each command
        foreach (var ScriptFile in scriptFiles)
        {
            // Determine which commands to run from this file
            IEnumerable<CommandBlock> commandsToRun;
            if (assertMode)
            {
                // Test mode: run ONLY assert commands
                commandsToRun = ScriptFile.Commands.Where(c => IsCallTo(c, "assert"));
            }
            else if (commandName != null)
            {
                // Run only matching named commands (legacy single-command mode)
                commandsToRun = ScriptFile.Commands.Where(c => c.IsCommand && string.Equals(c.Name, commandName, StringComparison.Ordinal));
            }
            else if (commandFilter != null)
            {
                // Run only commands whose name matches the filter (supports auto-derived names)
                // Also include parameterized command invocations (e.g., CHECK(var-usage))
                // whose argument name matches the filter
                commandsToRun = ScriptFile.Commands.Where(c =>
                    !IsCallTo(c, "save") && !IsCallTo(c, "assert") &&
                    (commandFilter.Contains(c.Name) || MatchesCommandFilter(c, commandFilter, allCommands)));
            }
            else
            {
                // Run all commands but skip save and assert (require explicit invocation)
                commandsToRun = ScriptFile.Commands.Where(c => !IsCallTo(c, "save") && !IsCallTo(c, "assert"));
            }

            // Expand command references into concrete blocks
            var expandedCommands = new List<CommandBlock>();
            foreach (var cmd in commandsToRun)
            {
                ExpandCommandRef(cmd, allCommands, expandedCommands, []);
            }

            foreach (var cmd in expandedCommands)
            {
                // Skip parameterized command DEFINITIONS — they're templates, not invocations
                if (cmd.Parameters is { Count: > 0 })
                    continue;

                // If OutputExpression is a function call that matches a parameterized command, resolve as invocation
                // e.g., CHECK(console-calls:notTest) -> bind console-calls:notTest to CHECK's parameter
                string? callTarget = cmd.OutputExpression is CallExpr callExpr ? callExpr.Name : cmd.ActionName;
                if (callTarget != null
                    && allCommands.TryGetValue(callTarget, out var targetCmds)
                    && targetCmds.Count > 0
                    && targetCmds[0].Parameters is { Count: > 0 })
                {
                    var target = targetCmds[0];
                    var tempLets = new Dictionary<string, LetDeclaration>(letDeclarations);

                    // Bind arguments from the CallExpr
                    if (cmd.OutputExpression is CallExpr ce && ce.Args.Count > 0)
                    {
                        for (int i = 0; i < Math.Min(target.Parameters.Count, ce.Args.Count); i++)
                        {
                            var paramName = target.Parameters[i];
                            var argExpr = ce.Args[i];
                            try
                            {
                                var (collection, filters, exclusions, pathOverride) = ScriptParser.DecomposeCollectionExpression(argExpr);
                                tempLets[paramName] = new LetDeclaration(paramName, collection, filters, cmd.Line)
                                {
                                    Exclusions = exclusions
                                };
                            }
                            catch (InvalidOperationException)
                            {
                                // Not a collection expression — store as value binding
                                tempLets[paramName] = new LetDeclaration(paramName, "", [], cmd.Line, ValueExpression: argExpr);
                            }
                        }
                    }
                    else if (cmd.Collection != null && target.Parameters.Count > 0)
                    {
                        tempLets[target.Parameters[0]] = new LetDeclaration(
                            target.Parameters[0], cmd.Collection, cmd.Filters, cmd.Line)
                        {
                            Exclusions = cmd.Exclusions
                        };
                    }
                    ExecuteCommand(target, documents, predicateGroups, tempLets, functionGroups, program, allCommands, allOutputs, fileOutputs, aggregateCounts, allAsserts);
                    continue;
                }

                ExecuteCommand(cmd, documents, predicateGroups, letDeclarations, functionGroups, program, allCommands, allOutputs, fileOutputs, aggregateCounts, allAsserts);
            }
        }

        var outputs = fileOutputs.Select(kv =>
            new FileOutput(kv.Key, string.Join(Environment.NewLine, kv.Value)))
            .ToList();

        // Execute RUN invocations
        foreach (var scriptFile in scriptFiles)
        {
            if (scriptFile.RunInvocations is null) continue;
            foreach (var run in scriptFile.RunInvocations)
            {
                // Look up the command by name
                if (!allCommands.TryGetValue(run.CommandName, out var cmdList) || cmdList.Count == 0)
                    continue;

                var cmdTemplate = cmdList[0];

                if (cmdTemplate.Parameters is { Count: > 0 } && run.Arguments.Count > 0)
                {
                    // Bind parameters as temporary let declarations
                    // e.g., command CHECK(violations) + RUN CHECK(var-usage)
                    // -> let violations = var-usage
                    var tempLets = new Dictionary<string, LetDeclaration>(letDeclarations);
                    for (int i = 0; i < Math.Min(cmdTemplate.Parameters.Count, run.Arguments.Count); i++)
                    {
                        var paramName = cmdTemplate.Parameters[i];
                        var argExpr = run.Arguments[i];
                        var (collection, filters, exclusions, pathOverride) = ScriptParser.DecomposeCollectionExpression(argExpr);
                        tempLets[paramName] = new LetDeclaration(paramName, collection, filters, run.Line, PathOverride: pathOverride)
                        {
                            Exclusions = exclusions
                        };
                    }

                    ExecuteCommand(cmdTemplate, documents, predicateGroups, tempLets, functionGroups, program, allCommands, allOutputs, fileOutputs, aggregateCounts, allAsserts);
                }
                else
                {
                    ExecuteCommand(cmdTemplate, documents, predicateGroups, letDeclarations, functionGroups, program, allCommands, allOutputs, fileOutputs, aggregateCounts, allAsserts);
                }
            }
        }

        // Execute action-lets: let bindings whose filters produce command objects
        // (Violations from toWarning, strings from toOutput, SaveActions from toSave, assertions from assert)
        {
            // Collect let names already consumed by explicit commands or RUN invocations (avoid double execution)
            var consumedLets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sf in scriptFiles)
            {
                foreach (var c in sf.Commands)
                {
                    if (c.Collection != null)
                        consumedLets.Add(c.Collection);
                    // Also track collections consumed via CallExpr-based command invocations
                    // e.g., CHECK(var-usage - Accepted) → "var-usage" is consumed
                    if (c.OutputExpression is CallExpr callExpr
                        && allCommands.TryGetValue(callExpr.Name, out var cmdList)
                        && cmdList.Count > 0
                        && cmdList[0].Parameters is { Count: > 0 })
                    {
                        foreach (var arg in callExpr.Args)
                        {
                            try
                            {
                                var (col, _, _, _) = ScriptParser.DecomposeCollectionExpression(arg);
                                consumedLets.Add(col);
                            }
                            catch { }
                        }
                    }
                }
                if (sf.RunInvocations is not null)
                    foreach (var run in sf.RunInvocations)
                        foreach (var arg in run.Arguments)
                        {
                            try
                            {
                                var (collection, _, _, _) = ScriptParser.DecomposeCollectionExpression(arg);
                                consumedLets.Add(collection);
                            }
                            catch { }
                        }
            }

            foreach (var (name, letDecl) in letDeclarations)
            {
                if (letDecl.IsValueBinding || letDecl.IsCollectionUnion) continue;
                if (letDecl.Filters.Count == 0) continue;

                // Only auto-execute LOCAL action-lets (not from imported packages)
                if (letDecl.PackageName != null) continue;

                // Find terminal action filter
                var terminalFilter = letDecl.Filters[^1];
                if (terminalFilter is not CallExpr terminalCall || !IsActionFilter(terminalCall.Name)) continue;

                // Skip if already consumed by an explicit command
                if (consumedLets.Contains(name)) continue;

                // Respect commandName/commandFilter selection
                if (commandName != null && !string.Equals(name, commandName, StringComparison.Ordinal)) continue;
                if (commandFilter != null && !commandFilter.Contains(name)) continue;

                // assertMode: only run assert action-lets; normal mode: skip asserts
                bool isAssert = terminalCall.Name is "assert" or "assertEmpty";
                if (assertMode && !isAssert) continue;
                if (!assertMode && isAssert) continue;

                // Skip toSave in default mode (requires explicit invocation, like SAVE commands)
                if (!assertMode && commandName == null && commandFilter == null && terminalCall.Name == "toSave") continue;

                try
                {
                    // Resolve the collection with ALL filters applied (action filter produces command objects)
                    var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
                    var items = ResolveGlobalCollection(letDecl.BaseCollection, evaluator, predicateGroups, letDeclarations, functionGroups);
                    var itemType = ResolveItemType(letDecl.BaseCollection, predicateGroups, letDeclarations, functionGroups);
                    items = ApplyFilters(items, itemType, letDecl.Filters, evaluator, functionGroups);

                    if (letDecl.Exclusions != null)
                    {
                        var finalType = ResolveItemTypeAfterFilters(itemType, letDecl.Filters, functionGroups);
                        items = ApplyExclusions(items, finalType, letDecl.Exclusions, evaluator, letDeclarations);
                    }

                    // Execute based on action type
                    if (terminalCall.Name is "toWarning" or "toError" or "toInfo")
                    {
                        // Items are Violations — format with check template
                        foreach (var item in items)
                        {
                            var ctx = new EvaluationContext();
                            ctx.Capture("Violation", item);
                            ctx.Capture("item", item);
                            if (item is DataObject ao)
                                CaptureAlanObjectFields(ctx, ao);
                            var richMessage = ResolveTemplate(CheckOutputTemplate, ctx);
                            allOutputs.Add(new PrintOutput(richMessage));
                        }
                    }
                    else if (terminalCall.Name == "toOutput")
                    {
                        // Items are formatted strings from ApplyFilters native handling
                        foreach (var item in items)
                            allOutputs.Add(new PrintOutput(new RichString(new[] { new TextSpan(item?.ToString() ?? "") })));
                    }
                    else if (terminalCall.Name == "toSave")
                    {
                        // Items are SaveAction DataObjects with Path/Content
                        foreach (var item in items)
                        {
                            if (item is DataObject sa)
                            {
                                var path = sa.GetField("Path")?.ToString() ?? "";
                                var content = sa.GetField("Content")?.ToString() ?? "";
                                if (!fileOutputs.TryGetValue(path, out var lines)) { lines = []; fileOutputs[path] = lines; }
                                lines.Add(content);
                            }
                        }
                    }
                    else if (terminalCall.Name == "assert")
                    {
                        var msg = GetAssertMessage(terminalCall) ?? name;
                        allAsserts.Add(new AssertResult(name, items.Count > 0, msg, items.Count));
                    }
                    else if (terminalCall.Name == "assertEmpty")
                    {
                        var msg = GetAssertMessage(terminalCall) ?? name;
                        allAsserts.Add(new AssertResult(name, items.Count == 0, msg, items.Count));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    _diagLog?.Invoke($"[trace] action-let '{name}' failed: {ex.Message}");
                }
            }
        }

        outputs = fileOutputs.Select(kv =>
            new FileOutput(kv.Key, string.Join(Environment.NewLine, kv.Value)))
            .ToList();

        // Warn about empty root collections referenced by executed commands
        // Skip warning in assert mode — test results are the intended output
        var warnings = new List<string>(errors);
        if (!assertMode && allOutputs.Count == 0 && outputs.Count == 0)
        {
            // Only check collections from commands that actually executed (not all commands in all files)
            var referencedCollections = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sf in scriptFiles)
            {
                IEnumerable<CommandBlock> executed;
                if (commandName != null)
                    executed = sf.Commands.Where(c => c.IsCommand && string.Equals(c.Name, commandName, StringComparison.Ordinal));
                else if (commandFilter != null)
                    executed = sf.Commands.Where(c => commandFilter.Contains(c.Name));
                else
                    executed = sf.Commands.Where(c => c.Parameters is not { Count: > 0 });

                foreach (var cmd in executed)
                    if (cmd.Collection is not null)
                        referencedCollections.Add(cmd.Collection);
            }

            // Resolve all root provider collections. Only warn if ALL roots are empty —
            // if any root has data, zero output is from predicate filtering, not missing data.
            bool anyRootHasData = false;
            var emptyRoots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var col in referencedCollections)
            {
                var roots = ResolveRootCollections(col, letDeclarations);
                foreach (var root in roots)
                {
                    if (aggregateCounts.TryGetValue(root, out var count))
                    {
                        if (count > 0) anyRootHasData = true;
                        else emptyRoots.Add(root);
                    }
                    else if (!letDeclarations.ContainsKey(root))
                        emptyRoots.Add(root);
                }
            }

            if (emptyRoots.Count > 0 && !anyRootHasData)
            {
                var names = string.Join(", ", emptyRoots.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                warnings.Add($"Warning: No output produced. The following collections are empty: {names}. Check that you imported the correct provider (e.g., 'import csharp' instead of 'import code').");
            }
        }

        return new InterpreterResult(allOutputs, outputs, warnings, allAsserts);
    }

    /// <summary>
    /// Executes a streaming command: foreach streamingSource => transform => sink.
    /// Runs indefinitely until cancelled. Used for push-like providers (HTTP server, etc.).
    /// </summary>
    public async Task RunStreamingAsync(
        CommandBlock cmd,
        List<ScriptFile> scriptFiles,
        CancellationToken cancellationToken)
    {
        if (cmd.Collection is null)
            throw new InvalidOperationException("Streaming command must have a collection.");

        // Build predicate/function/let dictionaries from script files (with conflict detection)
        var symbolErrors = new List<string>();
        var (predicateGroups, functionGroups, letDeclarations) = BuildSymbolTables(scriptFiles, symbolErrors);
        // Streaming mode: log symbol conflicts but don't block execution
        foreach (var err in symbolErrors)
            _diagLog?.Invoke($"[diag] {err}");

        // Resolve streaming source: first try direct registry lookup, then let bindings
        var streamingSource = _typeRegistry.ResolveStreamingSource(cmd.Collection);
        if (streamingSource is null && letDeclarations.TryGetValue(cmd.Collection, out var letDecl))
        {
            // Evaluate the let binding — it may be source('providerName')
            var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
            var letValue = evaluator.EvaluateLetValue(letDecl);
            if (letValue is SourceProvider letSource)
                streamingSource = letSource;
        }
        if (streamingSource is null)
            throw new InvalidOperationException($"'{cmd.Collection}' is not a streaming collection.");

        // Resolve sink: first try direct registry lookup, then let bindings
        SinkProvider sink;
        if (cmd.Sink is not null)
        {
            var resolvedSink = _typeRegistry.ResolveSink(cmd.Sink.Name);
            if (resolvedSink is null && letDeclarations.TryGetValue(cmd.Sink.Name, out var sinkLetDecl))
            {
                var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
                var sinkValue = evaluator.EvaluateLetValue(sinkLetDecl);
                if (sinkValue is SinkProvider dataSink)
                    resolvedSink = dataSink;
            }
            sink = resolvedSink ?? ResolveSink(cmd.Sink);
        }
        else
        {
            sink = ConsoleWriteLineSink.Instance;
        }

        string itemType = ResolveItemType(cmd.Collection, predicateGroups, letDeclarations, functionGroups);
        string finalItemType = ResolveItemTypeAfterFilters(itemType, cmd.Filters, functionGroups);

        try
        {
            if (cmd.IsAsync)
            {
                // Async mode: process items concurrently
                var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
                var activeTasks = new List<Task>();

                await foreach (var item in streamingSource.QueryStream(new ProviderQuery(), cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    await semaphore.WaitAsync(cancellationToken);
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessStreamItem(item, itemType, finalItemType, cmd, predicateGroups, letDeclarations, functionGroups, sink);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // If processing fails, try to complete the response with an error
                            // so the HTTP request doesn't hang indefinitely
                            TryFailResponseCompletion(item, ex);
                            _diagLog?.Invoke($"[diag] Stream item error: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    lock (activeTasks)
                    {
                        activeTasks.Add(task);
                        activeTasks.RemoveAll(t => t.IsCompleted);
                    }
                }

                // Wait for remaining tasks
                Task[] remaining;
                lock (activeTasks) { remaining = activeTasks.ToArray(); }
                await Task.WhenAll(remaining);
            }
            else
            {
                // Sync mode: process items sequentially
                await foreach (var item in streamingSource.QueryStream(new ProviderQuery(), cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await ProcessStreamItem(item, itemType, finalItemType, cmd, predicateGroups, letDeclarations, functionGroups, sink);
                }
            }
        }
        finally
        {
            await sink.CompleteAsync();
        }
    }

    /// <summary>
    /// Attempts to fail the response TCS on a streaming item so HTTP requests don't hang.
    /// Uses reflection since the TCS generic type is defined in the provider assembly.
    /// </summary>
    private static void TryFailResponseCompletion(object item, Exception ex)
    {
        if (item is not DataObject failedItem) return;
        var tcs = failedItem.GetField("__responseCompletion");
        if (tcs is null) return;
        // Use reflection to call TrySetException on the TCS (generic type is in provider assembly)
        var method = tcs.GetType().GetMethod("TrySetException", [typeof(Exception)]);
        method?.Invoke(tcs, [ex]);
    }

    private async Task ProcessStreamItem(
        object item,
        string itemType,
        string finalItemType,
        CommandBlock cmd,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<FunctionDefinition>> functionGroups,
        SinkProvider sink)
    {
        var evaluator = new PredicateEvaluator(predicateGroups, "", _typeRegistry, letDeclarations, functionGroups);

        // Apply filters
        var items = new List<object> { item };
        items = ApplyFilters(items, itemType, cmd.Filters, evaluator, functionGroups);
        if (items.Count == 0) return;

        var filteredItem = items[0];

        // Error dispatch: try to resolve transform function for "Error" type
        if (ErrorValue.IsError(filteredItem))
        {
            // Check if the transform function has an overload for "Error" type
            string? transformName = cmd.OutputExpression switch
            {
                IdentifierExpr id => id.Name,
                _ => null
            };

            if (transformName is not null
                && functionGroups.TryGetValue(transformName, out var errorFuncGroup)
                && errorFuncGroup.Any(f => string.Equals(f.InputType, "Error", StringComparison.OrdinalIgnoreCase)))
            {
                // Call the Error overload — user-defined error handler
                var errorResult = evaluator.EvaluateField(cmd.OutputExpression, filteredItem, "Error");
                if (errorResult is null) return; // null = swallow error (drop from pipeline)
                await sink.WriteAsync(filteredItem, errorResult);
            }
            else
            {
                // No error handler defined — pass ErrorValue directly to sink
                await sink.WriteAsync(filteredItem, filteredItem);
            }
            return;
        }

        // Evaluate transform
        object result;
        if (cmd.OutputExpression is not null)
        {
            result = evaluator.EvaluateField(cmd.OutputExpression, filteredItem, finalItemType) ?? filteredItem;
        }
        else if (!string.IsNullOrEmpty(cmd.MessageTemplate))
        {
            EvaluationContext ctx = new();
            ctx.Capture(finalItemType, filteredItem);
            ctx.Capture("item", filteredItem);
            if (filteredItem is DataObject ao)
                CaptureAlanObjectFields(ctx, ao);
            CaptureLetValues(ctx, evaluator, letDeclarations, filteredItem, finalItemType);
            var richMessage = ResolveTemplate(cmd.MessageTemplate, ctx);
            result = richMessage.ToPlainText();
        }
        else
        {
            result = filteredItem;
        }

        // If transform produced an error, propagate it to sink
        if (ErrorValue.IsError(result))
        {
            await sink.WriteAsync(filteredItem, result);
            return;
        }

        // Dispatch to sink
        await sink.WriteAsync(filteredItem, result);
    }

    /// <summary>
    /// Execute a single command block against all relevant documents.
    /// </summary>
    private void ExecuteCommand(
        CommandBlock cmd,
        List<Document> documents,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<FunctionDefinition>> functionGroups,
        ProgramInfo program,
        Dictionary<string, List<CommandBlock>> allCommands,
        List<PrintOutput> allOutputs,
        Dictionary<string, List<string>> fileOutputs,
        Dictionary<string, int> aggregateCounts,
        List<AssertResult> allAsserts)
    {
        // Evaluate guard predicate if present
        if (cmd.Guard is not null)
        {
            if (!EvaluateGuard(cmd.Guard, program, predicateGroups, letDeclarations, functionGroups))
                return;
        }

        // Bare command — no collection, execute once
        if (cmd.Collection is null)
        {
            RichString richMessage;
            if (cmd.OutputExpression is CallExpr { Target: null } printCall
                && printCall.Name is "print"
                && printCall.Args.Count >= 1
                && printCall.Args[0] is LiteralExpr { Value: string template })
            {
                // print('template') — resolve template with let bindings
                EvaluationContext ctx = new();
                var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
                CaptureLetValues(ctx, evaluator, letDeclarations, null!, "");
                foreach (var (aggName, aggCount) in aggregateCounts)
                    ctx.Capture(aggName, aggCount);
                richMessage = ResolveTemplate(template, ctx);
            }
            else if (cmd.OutputExpression is not null && string.IsNullOrEmpty(cmd.MessageTemplate))
            {
                // Expression-based output: evaluate the expression directly
                // Pre-resolve collection lets so identifiers like 'apiText' (which use .text() transforms) are available
                var resolvedCollections = ResolveGlobalCollectionLetBindings(letDeclarations, predicateGroups, functionGroups);
                var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups, resolvedCollections);
                var value = evaluator.EvaluateField(cmd.OutputExpression, null!, "");

                // Null return = side-effecting intrinsic (print/save/debug/assert) already handled output
                if (value is null)
                    return;

                // If value is a list, iterate and output each item separately
                if (value is IList listValue)
                {
                    foreach (var item in listValue)
                    {
                        var itemText = ConvertToText(item);
                        allOutputs.Add(new PrintOutput(new RichString(new[] { new TextSpan(itemText) })));
                    }
                    return;
                }

                richMessage = new RichString(new[] { new TextSpan(ConvertToText(value)) });
            }
            else if (!string.IsNullOrEmpty(cmd.MessageTemplate))
            {
                // Template output: resolve with let bindings and aggregate counts
                EvaluationContext ctx = new();
                var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
                CaptureLetValues(ctx, evaluator, letDeclarations, null!, "");
                // Also add aggregate counts as context variables
                foreach (var (aggName, aggCount) in aggregateCounts)
                    ctx.Capture(aggName, aggCount);
                richMessage = ResolveTemplate(cmd.MessageTemplate, ctx);
            }
            else
            {
                richMessage = new RichString(new[] { new TextSpan("") });
            }

            allOutputs.Add(new PrintOutput(richMessage));
            return;
        }

        var sw = Stopwatch.StartNew();
        int count = 0;

        string itemType = ResolveItemType(cmd.Collection, predicateGroups, letDeclarations, functionGroups);
        string finalItemType = ResolveItemTypeAfterFilters(itemType, cmd.Filters, functionGroups);

        bool isGlobal = IsGlobalRootCollection(cmd.Collection, predicateGroups, letDeclarations);

        // Global collections are processed once (not per-source-file)
        if (isGlobal)
        {
            // Pre-resolve intermediate collection lets so cross-collection predicates work
            var resolvedCollections = ResolveGlobalCollectionLetBindings(letDeclarations, predicateGroups, functionGroups);
            var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups, resolvedCollections);
            List<object> items;

            if (cmd.PathOverride is not null && _providerQueryService is not null)
            {
                var dotIdx = cmd.Collection.IndexOf('.');
                if (dotIdx >= 0)
                {
                    var prov = cmd.Collection[..dotIdx];
                    var coll = cmd.Collection[(dotIdx + 1)..];
                    items = _providerQueryService.Query(prov, coll, cmd.PathOverride);
                }
                else
                {
                    var ns = _typeRegistry.ResolveCollectionNamespace(cmd.Collection);
                    if (ns is not null)
                        items = _providerQueryService.Query(ns, cmd.Collection, cmd.PathOverride);
                    else
                        items = ResolveGlobalCollection(cmd.Collection, evaluator, predicateGroups, letDeclarations, functionGroups);
                }
            }
            else
            {
                items = ResolveGlobalCollection(cmd.Collection, evaluator, predicateGroups, letDeclarations, functionGroups);
            }

            _diagLog?.Invoke($"[trace] resolve: {cmd.Collection} -> {items.Count} items");
            items = ApplyFilters(items, itemType, cmd.Filters, evaluator, functionGroups);

            if (cmd.Exclusions != null)
                items = ApplyExclusions(items, finalItemType, cmd.Exclusions, evaluator, letDeclarations);

            _diagLog?.Invoke($"[trace] foreach: {cmd.Name ?? "command"} iterating {items.Count} items");

            foreach (var item in items)
            {
                if (count >= _maxOutputsPerCommand) break;
                if (sw.Elapsed > _timeout) break;

                // Error propagation in batch foreach: skip transforms, pass to sink directly
                if (ErrorValue.IsError(item))
                {
                    if (cmd.Sink is not null)
                    {
                        var sink = ResolveSink(cmd.Sink, _globalResolvedSelects);
                        sink.WriteAsync(item, item).GetAwaiter().GetResult();
                    }
                    else
                    {
                        var errMsg = item is ErrorValue ev ? ev.GetField("Message")?.ToString() ?? "error" : "error";
                        allOutputs.Add(new PrintOutput(new RichString(new[] { new TextSpan($"ERROR: {errMsg}") })));
                    }
                    count++;
                    continue;
                }

                EvaluationContext finalCtx = new();
                finalCtx.Capture(finalItemType, item);
                finalCtx.Capture("item", item);
                if (item is DataObject ao)
                    CaptureAlanObjectFields(finalCtx, ao);
                CaptureLetValues(finalCtx, evaluator, letDeclarations, item, finalItemType);

                RichString richMessage;
                if (cmd.OutputExpression is CallExpr { Target: null } iterPrintCall
                    && iterPrintCall.Name is "print"
                    && iterPrintCall.Args.Count >= 1
                    && iterPrintCall.Args[0] is LiteralExpr { Value: string iterTemplate })
                {
                    // print('template') in foreach - resolve template per-item
                    richMessage = ResolveTemplate(iterTemplate, finalCtx);
                }
                else if (cmd.OutputExpression is not null && string.IsNullOrEmpty(cmd.MessageTemplate))
                {
                    var value = evaluator.EvaluateField(cmd.OutputExpression, item, finalItemType);
                    if (value is null) { count++; continue; } // intrinsic handled output
                    richMessage = new RichString(new[] { new TextSpan(ConvertToText(value)) });
                }
                else if (string.IsNullOrEmpty(cmd.MessageTemplate))
                {
                    // Bare collection expression — output item's text representation
                    richMessage = new RichString(new[] { new TextSpan(ConvertToText(item)) });
                }
                else
                {
                    richMessage = ResolveTemplate(cmd.MessageTemplate, finalCtx);
                }

                if (cmd.Sink is not null)
                {
                    var sink = ResolveSink(cmd.Sink, _globalResolvedSelects);
                    sink.WriteAsync(item, richMessage.ToPlainText()).GetAwaiter().GetResult();
                }
                else
                {
                    allOutputs.Add(new PrintOutput(richMessage));
                }
                count++;
            }
            return;
        }

        foreach (var document in documents)
        {
            if (sw.Elapsed > _timeout) break;

            // Pre-resolve collection let bindings — cached per document across commands
            Dictionary<string, IList>? resolvedCollections;
            if (_documentLetCache.TryGetValue(document.Path, out var cachedLets))
            {
                resolvedCollections = new Dictionary<string, IList>(cachedLets);
            }
            else
            {
                resolvedCollections = ResolveCollectionLetBindings(
                    letDeclarations, document, predicateGroups, functionGroups);
                if (resolvedCollections is not null)
                    _documentLetCache[document.Path] = new Dictionary<string, IList>(resolvedCollections);
            }

            // Merge globally-resolved collections (from :select() lets) into per-document bindings
            // Global selects override per-document versions since they aggregate across all documents
            if (_globalResolvedSelects is not null)
            {
                resolvedCollections ??= new Dictionary<string, IList>();
                foreach (var (key, value) in _globalResolvedSelects)
                {
                    resolvedCollections[key] = value;
                }
            }

            // Make document collections available inside predicates (e.g., Types.MethodNames)
            resolvedCollections ??= new Dictionary<string, IList>();
            foreach (var collName in _typeRegistry.GetDocumentCollectionNames())
            {
                if (resolvedCollections.ContainsKey(collName)) continue;
                var collItems = _typeRegistry.GetCollectionItems(collName, document);
                if (collItems is not null)
                    resolvedCollections[collName] = collItems;
            }

            var evaluator = CreateEvaluator(predicateGroups, document.Path, letDeclarations, functionGroups, resolvedCollections);

            List<object> items;
            try
            {
                if (cmd.PathOverride is not null && _providerQueryService is not null)
                {
                    // Path-scoped command collection: foreach csharp.Types('../path/') => ...
                    var dotIdx = cmd.Collection!.IndexOf('.');
                    if (dotIdx >= 0)
                    {
                        var prov = cmd.Collection[..dotIdx];
                        var coll = cmd.Collection[(dotIdx + 1)..];
                        items = _providerQueryService.Query(prov, coll, cmd.PathOverride);
                    }
                    else
                    {
                        items = ResolveCollection(cmd.Collection, document, evaluator, predicateGroups, letDeclarations, functionGroups);
                    }
                }
                else
                {
                    items = ResolveCollection(cmd.Collection, document, evaluator, predicateGroups, letDeclarations, functionGroups);
                }
            }
            catch (InvalidOperationException) when (cmd.OutputExpression is not null)
            {
                // Collection couldn't be resolved — break out and let the expression fallback handle it
                break;
            }

            items = ApplyFilters(items, itemType, cmd.Filters, evaluator, functionGroups);

            if (cmd.Exclusions != null)
                items = ApplyExclusions(items, finalItemType, cmd.Exclusions, evaluator, letDeclarations);

            foreach (var item in items)
            {
                if (count >= _maxOutputsPerCommand) break;
                if (sw.Elapsed > _timeout) break;

                EvaluationContext finalCtx = new();
                finalCtx.Capture(finalItemType, item);
                finalCtx.Capture("item", item);
                if (item is DataObject ao)
                    CaptureAlanObjectFields(finalCtx, ao);
                CaptureLetValues(finalCtx, evaluator, letDeclarations, item, finalItemType);

                RichString richMessage;
                if (cmd.OutputExpression is CallExpr { Target: null } iterPrintCall
                    && iterPrintCall.Name is "print"
                    && iterPrintCall.Args.Count >= 1
                    && iterPrintCall.Args[0] is LiteralExpr { Value: string iterTemplate })
                {
                    // print('template') in foreach - resolve template per-item
                    richMessage = ResolveTemplate(iterTemplate, finalCtx);
                }
                else if (cmd.OutputExpression is not null && string.IsNullOrEmpty(cmd.MessageTemplate))
                {
                    var value = evaluator.EvaluateField(cmd.OutputExpression, item, finalItemType);
                    if (value is null) { count++; continue; } // intrinsic handled output
                    richMessage = new RichString(new[] { new TextSpan(ConvertToText(value)) });
                }
                else if (string.IsNullOrEmpty(cmd.MessageTemplate))
                {
                    // Bare collection expression — output item's text representation
                    richMessage = new RichString(new[] { new TextSpan(ConvertToText(item)) });
                }
                else
                {
                    richMessage = ResolveTemplate(cmd.MessageTemplate, finalCtx);
                }
                if (cmd.Sink is not null)
                {
                    var sink = ResolveSink(cmd.Sink, resolvedCollections);
                    sink.WriteAsync(item, richMessage.ToPlainText()).GetAwaiter().GetResult();
                }
                else
                {
                    allOutputs.Add(new PrintOutput(richMessage));
                }
                count++;
            }
        }

        // Fallback: if collection iteration produced nothing and we have an OutputExpression,
        // evaluate it as a scalar/list expression (handles cases like a.Name where a is a variable,
        // or Types.Count where Types is a collection and Count is a property)
        if (count == 0 && cmd.OutputExpression is not null)
        {
            // Build resolvedCollections from all documents so collection identifiers (e.g., "Types")
            // are accessible as lists in the expression evaluator
            var fallbackCollections = new Dictionary<string, IList>();
            foreach (var collName in _typeRegistry.GetDocumentCollectionNames())
            {
                var allItems = new List<object>();
                foreach (var doc in documents)
                {
                    var docItems = _typeRegistry.GetCollectionItems(collName, doc);
                    if (docItems is not null)
                        allItems.AddRange(docItems);
                }
                fallbackCollections[collName] = allItems;
            }

            var fallbackEvaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups, fallbackCollections);
            try
            {
                var value = fallbackEvaluator.EvaluateField(cmd.OutputExpression, null!, "");
                if (value is IList fallbackList)
                {
                    foreach (var item in fallbackList)
                        allOutputs.Add(new PrintOutput(new RichString(new[] { new TextSpan(ConvertToText(item)) })));
                }
                else
                {
                    allOutputs.Add(new PrintOutput(new RichString(new[] { new TextSpan(ConvertToText(value)) })));
                }
                // Mark as resolved so empty-collection warning is suppressed
                aggregateCounts[cmd.Collection!] = 1;
            }
            catch
            {
                // Expression evaluation failed — let the empty-collection warning fire
            }
        }
    }

    private List<object> ResolveCollection(
        string collection, Document document,
        PredicateEvaluator evaluator,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<FunctionDefinition>> functionGroups,
        HashSet<string>? visited = null,
        bool useQueryCache = true)
    {
        // Dotted value-binding access: codebase.Types, types.Count, types.First
        var dottedItems = TryResolveDottedValueBinding(collection, letDeclarations, evaluator);
        if (dottedItems != null) return dottedItems;

        // Resolve dotted collection names (e.g., "Source.Statements" -> "Statements")
        collection = ResolveDottedCollection(collection, letDeclarations);

        // Built-in collections
        if (TryGetBuiltinCollection(collection, document, out var items))
            return items;

        visited ??= [];
        if (!visited.Add(collection))
            throw new InvalidOperationException($"Circular collection reference: {collection}");

        // Let declaration: let Name = Base:filter1:filter2
        if (letDeclarations.TryGetValue(collection, out var letDecl))
        {
            // Collection union: let Name = [a, b, c] where each element is a collection
            if (letDecl.IsCollectionUnion)
            {
                var unionItems = new List<object>();
                foreach (var elem in ((CollectionUnionExpr)letDecl.ValueExpression!).Elements)
                {
                    var name = GetUnionElementName(elem);
                    unionItems.AddRange(ResolveCollection(name, document, evaluator, predicateGroups, letDeclarations, functionGroups, new(visited), useQueryCache));
                }
                return unionItems;
            }

            // Value bindings (let Name = [...]) — evaluate and return as list
            if (letDecl.IsValueBinding)
            {
                var value = evaluator.EvaluateField(letDecl.ValueExpression!, null!, "");
                if (value is IList list)
                    return list.Cast<object>().ToList();
                if (value is not null)
                    return [value];
                return [];
            }

            // Path-scoped collection: let x = csharp.Types('../path/'):filters
            // Query the provider directly with the path override instead of using global collections
            if (letDecl.PathOverride is not null && _providerQueryService is not null)
            {
                try
                {
                    return ResolvePathScopedCollection(letDecl, evaluator, predicateGroups, functionGroups, useQueryCache);
                }
                catch when (letDecl.SourceExpression is not null && letDecl.Filters.Count == 0)
                {
                    // Path-scoped resolution failed — this is actually a provider function call
                    // (e.g., csharp.Load('path')) not a path-scoped collection query.
                    // Fall through to SourceExpression evaluation below.
                }
            }

            List<object> baseItems;
            try
            {
                baseItems = ResolveCollection(
                    letDecl.BaseCollection, document, evaluator, predicateGroups, letDeclarations, functionGroups, visited, useQueryCache);
            }
            catch when (letDecl.SourceExpression is not null && letDecl.Filters.Count == 0)
            {
                // BaseCollection resolution failed but we have the original expression —
                // this let is actually a value expression (e.g., let count = typeNames.Count)
                throw new InvalidOperationException($"'{collection}' is a value binding, not a collection");
            }
            var baseItemType = ResolveItemType(letDecl.BaseCollection, predicateGroups, letDeclarations, functionGroups);

            // Extract pushdown hints from the filter chain.
            // Simple property checks (Public, Abstract, etc.) can be evaluated natively
            // by the TypeRegistry without going through the full PredicateEvaluator pipeline.
            var predicateNameSet = predicateGroups.Count > 0 ? new HashSet<string>(predicateGroups.Keys) : null;
            var itemTypeDesc = _typeRegistry.GetType(baseItemType);
            var (pushdownFilter, residualStart) = FilterHintExtractor.Extract(
                letDecl.Filters, itemTypeDesc, predicateNameSet, predicateGroups.Count > 0 ? predicateGroups : null);

            // If we extracted pushdown hints, pre-filter the base items natively
            if (pushdownFilter is not null)
            {
                baseItems = _typeRegistry.ApplyPushdownFilter(baseItemType, baseItems, pushdownFilter);
            }

            // Build residual filter list (filters not pushed down to the provider)
            var residualFilters = residualStart > 0
                ? letDecl.Filters.GetRange(residualStart, letDecl.Filters.Count - residualStart)
                : letDecl.Filters;

            // Fingerprint-based cache: order-independent dedup for filter chains
            // Note: fingerprint uses FULL filter chain for cache identity, but execution
            // uses residualFilters (pushdown-filtered items + remaining filters)
            if (useQueryCache)
            {
                var functionNameSet = functionGroups.Count > 0 ? new HashSet<string>(functionGroups.Keys) : null;
                var fingerprint = QueryFingerprint.Compute(letDecl.BaseCollection, letDecl.Filters, document.Path, functionNameSet, letDecl.PathOverride);
                if (letDecl.Exclusions != null)
                    fingerprint += "|!" + QueryFingerprint.Serialize(letDecl.Exclusions);

                if (_queryCache.TryGetValue(fingerprint, out var cached))
                    return cached;

                // Apply residual filters (pushdown-able ones already applied natively)
                var result = ApplyFilters(baseItems, baseItemType, residualFilters, evaluator, functionGroups);

                // Apply set subtraction if exclusions are specified
                if (letDecl.Exclusions != null)
                {
                    var finalType = ResolveItemTypeAfterFilters(baseItemType, letDecl.Filters, functionGroups);
                    result = ApplyExclusions(result, finalType, letDecl.Exclusions, evaluator, letDeclarations);
                }

                _queryCache.Set(fingerprint, result);
                return result;
            }

            // No caching — resolve directly
            {
                var result = ApplyFilters(baseItems, baseItemType, residualFilters, evaluator, functionGroups);

                if (letDecl.Exclusions != null)
                {
                    var finalType = ResolveItemTypeAfterFilters(baseItemType, letDecl.Filters, functionGroups);
                    result = ApplyExclusions(result, finalType, letDecl.Exclusions, evaluator, letDeclarations);
                }

                return result;
            }
        }

        // Derived collection from predicate
        if (predicateGroups.TryGetValue(collection, out var preds))
        {
            var pred = preds[0];
            var baseItems = ResolveCollection(
                pred.ParameterType, document, evaluator, predicateGroups, letDeclarations, functionGroups, visited);
            var baseItemType = ResolveItemType(pred.ParameterType, predicateGroups, letDeclarations, functionGroups);

            return baseItems.Where(item =>
            {
                var (result, _) = evaluator.EvaluateAsBool(pred.Body, item, baseItemType);
                return result;
            }).ToList();
        }

        throw new InvalidOperationException($"Unknown collection '{collection}'");
    }

    /// <summary>
    /// Resolves a path-scoped collection by querying the provider service.
    /// The base collection name (e.g., "csharp.Types") is split into provider + collection,
    /// queried with the path override, and then filters are applied.
    /// </summary>
    private List<object> ResolvePathScopedCollection(
        LetDeclaration letDecl,
        PredicateEvaluator evaluator,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, List<FunctionDefinition>> functionGroups,
        bool useQueryCache)
    {
        // Fingerprint cache check
        if (useQueryCache)
        {
            var functionNameSet = functionGroups.Count > 0 ? new HashSet<string>(functionGroups.Keys) : null;
            var fingerprint = QueryFingerprint.Compute(letDecl.BaseCollection, letDecl.Filters, null, functionNameSet, letDecl.PathOverride);
            if (letDecl.Exclusions != null)
                fingerprint += "|!" + QueryFingerprint.Serialize(letDecl.Exclusions);

            if (_queryCache.TryGetValue(fingerprint, out var cached))
                return cached;

            var result = ResolvePathScopedCollectionCore(letDecl, evaluator, predicateGroups, functionGroups);
            _queryCache.Set(fingerprint, result);
            return result;
        }

        return ResolvePathScopedCollectionCore(letDecl, evaluator, predicateGroups, functionGroups);
    }

    private List<object> ResolvePathScopedCollectionCore(
        LetDeclaration letDecl,
        PredicateEvaluator evaluator,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, List<FunctionDefinition>> functionGroups)
    {
        // Split "csharp.Types" into provider="csharp", collection="Types"
        var dotIndex = letDecl.BaseCollection.IndexOf('.');
        if (dotIndex < 0)
            throw new InvalidOperationException($"Path-scoped collection '{letDecl.BaseCollection}' must be qualified (e.g., csharp.Types)");

        var providerName = letDecl.BaseCollection[..dotIndex];
        var collectionName = letDecl.BaseCollection[(dotIndex + 1)..];

        var baseItems = _providerQueryService!.Query(providerName, collectionName, letDecl.PathOverride!);

        // Apply filters if any
        if (letDecl.Filters.Count == 0 && letDecl.Exclusions == null)
            return baseItems;

        var baseItemType = ResolveItemType(letDecl.BaseCollection, predicateGroups, new Dictionary<string, LetDeclaration>(), functionGroups);

        // Apply pushdown filter optimization
        var predicateNameSet = predicateGroups.Count > 0 ? new HashSet<string>(predicateGroups.Keys) : null;
        var itemTypeDesc = _typeRegistry.GetType(baseItemType);
        var (pushdownFilter, residualStart) = FilterHintExtractor.Extract(
            letDecl.Filters, itemTypeDesc, predicateNameSet, predicateGroups.Count > 0 ? predicateGroups : null);

        if (pushdownFilter is not null)
            baseItems = _typeRegistry.ApplyPushdownFilter(baseItemType, baseItems, pushdownFilter);

        var residualFilters = residualStart > 0
            ? letDecl.Filters.GetRange(residualStart, letDecl.Filters.Count - residualStart)
            : letDecl.Filters;

        var result = ApplyFilters(baseItems, baseItemType, residualFilters, evaluator, functionGroups);

        if (letDecl.Exclusions != null)
        {
            var finalType = ResolveItemTypeAfterFilters(baseItemType, letDecl.Filters, functionGroups);
            result = ApplyExclusions(result, finalType, letDecl.Exclusions, evaluator, new Dictionary<string, LetDeclaration>());
        }

        return result;
    }

    /// <summary>
    /// Pre-resolve collection let bindings (e.g., let factoryTypes = Source.Types:where(isFactory))
    /// so they can be accessed from within predicates. Value bindings and collection unions are skipped
    /// since they are already handled by the evaluator.
    /// </summary>
    private Dictionary<string, IList>? ResolveCollectionLetBindings(
        Dictionary<string, LetDeclaration> letDeclarations,
        Document document,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, List<FunctionDefinition>> functionGroups)
    {
        Dictionary<string, IList>? resolved = null;
        // Temporary evaluator without resolved collections for bootstrapping
        var bootstrapEvaluator = CreateEvaluator(predicateGroups, document.Path, letDeclarations, functionGroups);

        foreach (var (name, letDecl) in letDeclarations)
        {
            // Skip value bindings and collection unions — handled elsewhere
            if (letDecl.IsValueBinding || letDecl.IsCollectionUnion) continue;
            // Skip check-level lets (those with actions like :toWarning) — they are commands, not data
            if (letDecl.Filters.Any(f =>
                (f is CallExpr fc && IsActionFilter(fc.Name)))) continue;

            try
            {
                // Bootstrap uses no query cache — the bootstrap evaluator may produce
                // incorrect results for filters that reference unresolved collections
                // (the language-filter fallback returns false instead of throwing).
                var items = ResolveCollection(
                    name, document, bootstrapEvaluator, predicateGroups, letDeclarations, functionGroups, useQueryCache: false);
                resolved ??= new Dictionary<string, IList>();
                resolved[name] = items;
            }
            catch
            {
                // If resolution fails (e.g., depends on unresolved bindings), skip silently
            }
        }
        return resolved;
    }

    private static bool IsActionFilter(string name) =>
        name is "toError" or "toWarning" or "toInfo" or "toOutput" or "toSave" or "assert" or "assertEmpty";

    /// <summary>
    /// Output template for Violation objects (check results).
    /// </summary>
    private const string CheckOutputTemplate = "{item.File@dim}({item.Line@dim}): {item.Severity@auto}: {item.Message}";

    /// <summary>
    /// Extract assertion message from an assert/assertEmpty filter call.
    /// </summary>
    private static string? GetAssertMessage(CallExpr call) =>
        call.Args.Count > 0 && call.Args[0] is LiteralExpr lit ? lit.Value?.ToString() : null;

    /// <summary>
    /// Pre-resolve non-action collection let bindings for global commands.
    /// Same purpose as ResolveCollectionLetBindings but uses ResolveGlobalCollection
    /// (no Document context required).
    /// </summary>
    private Dictionary<string, IList>? ResolveGlobalCollectionLetBindings(
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, List<FunctionDefinition>> functionGroups)
    {
        Dictionary<string, IList>? resolved = null;
        var bootstrapEvaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);

        foreach (var (name, letDecl) in letDeclarations)
        {
            if (letDecl.IsValueBinding || letDecl.IsCollectionUnion) continue;
            if (letDecl.Filters.Any(f =>
                (f is CallExpr fc && IsActionFilter(fc.Name)))) continue;

            // Only pre-resolve "leaf" lets whose base is a direct global collection
            // or a value-binding let (e.g., export let Types = cb.Types).
            // Lets whose base is another non-value let may depend on cross-collection predicates
            // that the bootstrap evaluator cannot properly evaluate (it lacks _resolvedCollections).
            var resolvedBase = ResolveDottedCollection(letDecl.BaseCollection, letDeclarations);
            if (letDeclarations.TryGetValue(resolvedBase, out var baseLet) && !baseLet.IsValueBinding) continue;

            try
            {
                var items = ResolveGlobalCollection(
                    name, bootstrapEvaluator, predicateGroups, letDeclarations, functionGroups);
                resolved ??= new Dictionary<string, IList>();
                resolved[name] = items;
            }
            catch
            {
                // Skip — may depend on unresolved bindings
            }
        }
        return resolved;
    }

    /// <summary>
    /// Apply a chain of filters to a list of items. Each filter is either:
    /// - A predicate (Where): keeps items matching the predicate
    /// - A function (Select/Map): transforms each item into a new typed object
    /// </summary>
    private List<object> ApplyFilters(
        List<object> items, string itemType, List<Expression> filters,
        PredicateEvaluator evaluator,
        Dictionary<string, List<FunctionDefinition>> functionGroups)
    {
        IEnumerable<object> current = items;
        var currentType = itemType;
        int beforeCount = items.Count;

        foreach (var filter in filters)
        {
            // Detect if this filter is a function call
            var funcName = GetFunctionNameFromFilter(filter);

            // Handle .Select() — project each item to a value
            if (funcName == "Select")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    // Barrier: materialize before projection (type changes)
                    var materialized = current.Where(item => item is not null).ToList();
                    current = materialized.Select(item =>
                    {
                        var value = evaluator.EvaluateField(fieldArgs[0], item, "item");
                        return value ?? (object)"";
                    }).ToList();
                    _diagLog?.Invoke($"[trace] filter: .Select -> {materialized.Count} -> {((List<object>)current).Count} items");
                    currentType = "object";
                    continue;
                }
            }
            // Handle .OrderBy() — sort items by expression ascending
            else if (funcName == "OrderBy")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    var materialized = current.Where(item => item is not null).ToList();
                    materialized.Sort((a, b) =>
                    {
                        var aVal = evaluator.EvaluateField(fieldArgs[0], a, "item");
                        var bVal = evaluator.EvaluateField(fieldArgs[0], b, "item");
                        return CompareForSort(aVal, bVal);
                    });
                    current = materialized;
                    _diagLog?.Invoke($"[trace] filter: .OrderBy -> {materialized.Count} items");
                    continue;
                }
            }
            // Handle .OrderByDescending() — sort items by expression descending
            else if (funcName == "OrderByDescending")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    var materialized = current.Where(item => item is not null).ToList();
                    materialized.Sort((a, b) =>
                    {
                        var aVal = evaluator.EvaluateField(fieldArgs[0], a, "item");
                        var bVal = evaluator.EvaluateField(fieldArgs[0], b, "item");
                        return CompareForSort(bVal, aVal); // reversed
                    });
                    current = materialized;
                    _diagLog?.Invoke($"[trace] filter: .OrderByDescending -> {materialized.Count} items");
                    continue;
                }
            }
            // Handle .Sum() — aggregate numeric values
            else if (funcName == "Sum")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    double sum = 0;
                    foreach (var item in current)
                    {
                        if (item is null) continue;
                        var val = evaluator.EvaluateField(fieldArgs[0], item, "item");
                        sum += ToDouble(val);
                    }
                    object result = (int)sum == sum ? (object)(int)sum : sum;
                    current = [result];
                    currentType = "int";
                    _diagLog?.Invoke($"[trace] filter: .Sum -> {result}");
                    continue;
                }
            }
            // Handle .Min() — find minimum numeric value
            else if (funcName == "Min")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    double? min = null;
                    foreach (var item in current)
                    {
                        if (item is null) continue;
                        var val = ToDouble(evaluator.EvaluateField(fieldArgs[0], item, "item"));
                        if (min is null || val < min) min = val;
                    }
                    object result = min is null ? 0 : ((int)min.Value == min.Value ? (object)(int)min.Value : min.Value);
                    current = [result];
                    currentType = "int";
                    _diagLog?.Invoke($"[trace] filter: .Min -> {result}");
                    continue;
                }
            }
            // Handle .Max() — find maximum numeric value
            else if (funcName == "Max")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    double? max = null;
                    foreach (var item in current)
                    {
                        if (item is null) continue;
                        var val = ToDouble(evaluator.EvaluateField(fieldArgs[0], item, "item"));
                        if (max is null || val > max) max = val;
                    }
                    object result = max is null ? 0 : ((int)max.Value == max.Value ? (object)(int)max.Value : max.Value);
                    current = [result];
                    currentType = "int";
                    _diagLog?.Invoke($"[trace] filter: .Max -> {result}");
                    continue;
                }
            }
            // Handle .Average() — compute average numeric value
            else if (funcName == "Average")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    double sum = 0;
                    int count = 0;
                    foreach (var item in current)
                    {
                        if (item is null) continue;
                        sum += ToDouble(evaluator.EvaluateField(fieldArgs[0], item, "item"));
                        count++;
                    }
                    object result = count > 0 ? sum / count : 0.0;
                    current = [result];
                    currentType = "double";
                    _diagLog?.Invoke($"[trace] filter: .Average -> {result}");
                    continue;
                }
            }
            // Handle .Distinct() — deduplicate items
            else if (funcName == "Distinct")
            {
                var fieldArgs = GetFilterArgs(filter);
                var materialized = current.Where(item => item is not null).ToList();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var result = new List<object>();
                foreach (var item in materialized)
                {
                    string key;
                    if (fieldArgs.Count > 0)
                        key = evaluator.EvaluateField(fieldArgs[0], item, "item")?.ToString() ?? "";
                    else
                        key = item.ToString() ?? "";
                    if (seen.Add(key))
                        result.Add(item);
                }
                current = result;
                _diagLog?.Invoke($"[trace] filter: .Distinct -> {materialized.Count} -> {result.Count} items");
                continue;
            }
            // Handle .GroupBy() — group items by expression
            else if (funcName == "GroupBy")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    var groups = new Dictionary<string, List<object>>(StringComparer.Ordinal);
                    var groupOrder = new List<string>();
                    foreach (var item in current)
                    {
                        if (item is null) continue;
                        var key = evaluator.EvaluateField(fieldArgs[0], item, "item")?.ToString() ?? "";
                        if (!groups.TryGetValue(key, out var groupList))
                        {
                            groupList = new List<object>();
                            groups[key] = groupList;
                            groupOrder.Add(key);
                        }
                        groupList.Add(item);
                    }
                    var result = new List<object>();
                    foreach (var key in groupOrder)
                    {
                        var groupObj = new DataObject("Group");
                        groupObj.Set("Key", key);
                        groupObj.Set("Items", groups[key]);
                        groupObj.Set("Count", groups[key].Count);
                        result.Add(groupObj);
                    }
                    current = result;
                    currentType = "Group";
                    _diagLog?.Invoke($"[trace] filter: .GroupBy -> {result.Count} groups");
                    continue;
                }
            }
            // Handle .Reduce() — aggregate items with operator
            else if (funcName == "Reduce")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count >= 2)
                {
                    var opExpr = fieldArgs[0];
                    var fieldExpr = fieldArgs[1];
                    var separator = fieldArgs.Count > 2 ? evaluator.EvaluateField(fieldArgs[2], null!, "item")?.ToString() ?? "" : "";

                    var op = opExpr is LiteralExpr lit ? lit.Value?.ToString() :
                             opExpr is IdentifierExpr id2 ? id2.Name : "+";

                    var values = new List<object?>();
                    foreach (var item in current)
                    {
                        if (item is null) continue;
                        values.Add(evaluator.EvaluateField(fieldExpr, item, "item"));
                    }

                    if (op == "+")
                    {
                        bool isNumeric = values.Count > 0 && values[0] is int or double;
                        if (isNumeric)
                        {
                            double sum = 0;
                            foreach (var val in values)
                                sum += ToDouble(val);
                            object result = (int)sum == sum ? (object)(int)sum : sum;
                            current = [result];
                        }
                        else
                        {
                            var parts = values.Where(v => v is not null).Select(v => v!.ToString()!).ToList();
                            current = [(object)string.Join(separator, parts)];
                        }
                    }
                    currentType = "object";
                    _diagLog?.Invoke($"[trace] filter: .Reduce -> 1 value");
                    continue;
                }
            }
            // Handle .text() — format each item with a template, join into a single string
            else if (funcName == "text")
            {
                var templateArgs = GetFilterArgs(filter);
                if (templateArgs.Count > 0 && templateArgs[0] is LiteralExpr litExpr && litExpr.Value is string template)
                {
                    // Barrier: materialize before text join
                    var lines = current.Where(item => item is not null)
                        .Select(item =>
                        {
                            var ctx = new EvaluationContext();
                            ctx.Capture(currentType, item);
                            ctx.Capture("item", item);
                            if (item is DataObject ao)
                                CaptureAlanObjectFields(ctx, ao);
                            return ResolveTemplate(template, ctx).ToPlainText();
                        })
                        .ToList();
                    _diagLog?.Invoke($"[trace] filter: .text -> {lines.Count} items -> 1 string");
                    current = [(object)string.Join(Environment.NewLine, lines)];
                    currentType = "string";
                    continue;
                }
            }
            // Handle :toOutput(template) — format each item with template, keep as individual strings
            else if (funcName == "toOutput")
            {
                var templateArgs = GetFilterArgs(filter);
                if (templateArgs.Count > 0 && templateArgs[0] is LiteralExpr litOut && litOut.Value is string outputTemplate)
                {
                    var lines = current.Where(item => item is not null)
                        .Select(item =>
                        {
                            var ctx = new EvaluationContext();
                            ctx.Capture(currentType, item);
                            ctx.Capture("item", item);
                            if (item is DataObject ao)
                                CaptureAlanObjectFields(ctx, ao);
                            return (object)ResolveTemplate(outputTemplate, ctx).ToPlainText();
                        }).ToList();
                    _diagLog?.Invoke($"[trace] filter: :toOutput -> {lines.Count} items");
                    current = lines;
                    currentType = "string";
                    continue;
                }
            }
            // Handle :toSave(file, template) — produce SaveAction objects with Path/Content
            else if (funcName == "toSave")
            {
                var saveArgs = GetFilterArgs(filter);
                if (saveArgs.Count > 0)
                {
                    string filePath = (saveArgs[0] as LiteralExpr)?.Value?.ToString() ?? "";
                    string saveTemplate = saveArgs.Count > 1
                        ? (saveArgs[1] as LiteralExpr)?.Value?.ToString() ?? "{item}"
                        : "{item}";
                    var results = current.Where(item => item is not null)
                        .Select(item =>
                        {
                            var ctx = new EvaluationContext();
                            ctx.Capture(currentType, item);
                            ctx.Capture("item", item);
                            if (item is DataObject ao)
                                CaptureAlanObjectFields(ctx, ao);
                            var content = ResolveTemplate(saveTemplate, ctx).ToPlainText();
                            var sa = new DataObject("SaveAction");
                            sa.Set("Path", filePath);
                            sa.Set("Content", content);
                            return (object)sa;
                        }).ToList();
                    _diagLog?.Invoke($"[trace] filter: :toSave -> {results.Count} items");
                    current = results;
                    currentType = "SaveAction";
                    continue;
                }
            }
            else if (funcName != null && functionGroups.ContainsKey(funcName))
            {
                // Barrier: function map transforms items (type changes)
                var funcArgs = GetFilterArgs(filter);
                var capturedType = currentType;
                var mapped = current.Select(item =>
                    evaluator.ApplyFunction(funcName, item, capturedType, funcArgs)!).ToList();
                currentType = evaluator.GetFunctionReturnType(funcName) ?? currentType;
                _diagLog?.Invoke($"[trace] filter: :{funcName} -> {beforeCount} -> {mapped.Count} items (-> {currentType})");
                current = mapped;
                beforeCount = mapped.Count;
            }
            else if (funcName != null && evaluator.IsClosureLet(funcName))
            {
                // Closure (partially-applied function) used as a transform filter
                var funcArgs = GetFilterArgs(filter);
                var capturedType = currentType;
                var mapped = current.Select(item =>
                    evaluator.ApplyClosureFilter(funcName, item, capturedType, funcArgs)!).ToList();
                _diagLog?.Invoke($"[trace] filter: :{funcName}(closure) -> {beforeCount} -> {mapped.Count} items");
                current = mapped;
                beforeCount = mapped.Count;
            }
            else
            {
                // Predicate filter
                if (_diagLog is not null)
                {
                    // Materialize to get counts for trace output
                    var capturedFilter = filter;
                    var materialized = current.Where(item =>
                    {
                        try
                        {
                            var (result, _) = evaluator.EvaluateAsBool(capturedFilter, item, "item");
                            return result;
                        }
                        catch (InvalidOperationException)
                        {
                            // Unknown identifier in filter position — treat as non-match
                            return false;
                        }
                    }).ToList();
                    var filterName = GetFilterDisplayName(filter);
                    _diagLog($"[trace] filter: :{filterName} -> {beforeCount} -> {materialized.Count} items");
                    beforeCount = materialized.Count;
                    current = materialized;
                }
                else
                {
                    // No tracing: compose lazily — no materialization
                    var capturedFilter = filter;
                    current = current.Where(item =>
                    {
                        try
                        {
                            var (result, _) = evaluator.EvaluateAsBool(capturedFilter, item, "item");
                            return result;
                        }
                        catch (InvalidOperationException)
                        {
                            // Unknown identifier in filter position — treat as non-match
                            return false;
                        }
                    });
                }
            }
        }

        // Single materialization point for the entire filter chain
        return current as List<object> ?? current.ToList();
    }

    /// <summary>
    /// Apply set subtraction: remove items whose Source matches any string in the exclusion list.
    /// Evaluates the exclusion expression to get a list of strings, then filters items by Source property.
    /// </summary>
    private List<object> ApplyExclusions(
        List<object> items, string itemType, Expression exclusionExpr,
        PredicateEvaluator evaluator,
        Dictionary<string, LetDeclaration> letDeclarations)
    {
        var exclusionSet = ResolveExclusionSet(exclusionExpr, letDeclarations);
        if (exclusionSet.Count == 0)
            return items;

        return items.Where(item =>
        {
            var source = GetItemSource(item, itemType);
            return source == null || !exclusionSet.Contains(source);
        }).ToList();
    }

    /// <summary>
    /// Resolve an exclusion expression to a set of strings.
    /// Supports: IdentifierExpr (let-bound list), ListLiteralExpr (inline list).
    /// </summary>
    private HashSet<string> ResolveExclusionSet(Expression expr, Dictionary<string, LetDeclaration> letDeclarations)
    {
        if (expr is IdentifierExpr id && letDeclarations.TryGetValue(id.Name, out var letDecl) && letDecl.IsValueBinding)
        {
            if (letDecl.ValueExpression is ListLiteralExpr list)
                return list.Elements.Select(e => e is LiteralExpr lit ? lit.Value?.ToString() ?? "" : "").ToHashSet();
        }

        if (expr is ListLiteralExpr inlineList)
            return inlineList.Elements.Select(e => e is LiteralExpr lit ? lit.Value?.ToString() ?? "" : "").ToHashSet();

        return [];
    }

    /// <summary>
    /// Get the Source property from an item for set subtraction matching.
    /// For AlanObjects, reads the Source field. For CLR objects, uses the type registry.
    /// </summary>
    private string? GetItemSource(object item, string itemType)
    {
        if (item is DataObject ao)
            return ao.GetField("Source")?.ToString();

        var typeName = _typeRegistry.InferTypeName(item) ?? itemType;
        var desc = _typeRegistry.GetType(typeName)?.GetProperty("Source");
        if (desc?.Accessor is not null)
            return desc.Accessor(item)?.ToString();

        return null;
    }

    /// <summary>
    /// Extract function name from a filter expression, if it could be a function call.
    /// Returns null if it's an expression that can't be a function.
    /// </summary>
    private static string? GetFunctionNameFromFilter(Expression filter)
    {
        return filter switch
        {
            CallExpr c => c.Name,
            IdentifierExpr id => id.Name,
            _ => null
        };
    }

    /// <summary>
    /// Extract call arguments from a filter expression.
    /// </summary>
    private static List<Expression> GetFilterArgs(Expression filter)
    {
        return filter switch
        {
            CallExpr c => c.Args,
            _ => []
        };
    }

    /// <summary>
    /// Get a human-readable display name for a filter expression (for trace output).
    /// </summary>
    private static string GetFilterDisplayName(Expression filter)
    {
        return filter switch
        {
            CallExpr c => c.Name,
            IdentifierExpr id => id.Name,
            MemberAccessExpr ma => ma.Member,
            UnaryExpr { Operator: "!" } neg => $"!{GetFilterDisplayName(neg.Operand)}",
            _ => filter.ToString() ?? "filter"
        };
    }

    private static double ToDouble(object? value) => value switch
    {
        int i => i,
        double d => d,
        bool b => b ? 1.0 : 0.0,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) => n,
        _ => 0.0
    };

    private static int CompareForSort(object? a, object? b)
    {
        if (a is int ai && b is int bi) return ai.CompareTo(bi);
        if (a is double or int && b is double or int) return ToDouble(a).CompareTo(ToDouble(b));
        var sa = a?.ToString() ?? "";
        var sb = b?.ToString() ?? "";
        return string.Compare(sa, sb, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pre-evaluates let value bindings (non-collection expressions) and captures them into a context.
    /// This allows templates to reference {letName} for scalar values.
    /// </summary>
    private void CaptureLetValues(EvaluationContext ctx, PredicateEvaluator evaluator,
        Dictionary<string, LetDeclaration> letDeclarations, object item, string paramType)
    {
        foreach (var (name, letDecl) in letDeclarations)
        {
            // Explicit value bindings (let Name = [...], let Name = expr)
            if (letDecl.IsValueBinding)
            {
                if (letDecl.IsCollectionUnion) continue;
                try
                {
                    var value = evaluator.EvaluateField(letDecl.ValueExpression!, item, paramType);
                    if (value is not null)
                        ctx.Capture(name, value);
                }
                catch { /* skip let values that fail to evaluate in this context */ }
                continue;
            }

            // Non-collection lets with SourceExpression fallback (e.g., let count = typeNames.Count)
            if (letDecl.SourceExpression is not null)
            {
                try
                {
                    var value = evaluator.EvaluateField(letDecl.SourceExpression, item, paramType);
                    if (value is not null)
                        ctx.Capture(name, value);
                }
                catch { /* not evaluable as expression — it's a real collection, skip */ }
            }
        }
    }

    private bool TryGetBuiltinCollection(string collection, Document document, out List<object> items)
    {
        var result = _typeRegistry.GetCollectionItems(collection, document);
        if (result is not null)
        {
            items = result;
            return true;
        }

        items = [];
        return false;
    }

    /// <summary>
    /// Resolves a dotted collection name (e.g., "Source.Statements") to its property collection name.
    /// Validates that the parent object exists as a let binding and resolves the property
    /// to the corresponding collection name. Non-dotted names pass through unchanged.
    /// </summary>
    private string ResolveDottedCollection(string collection, Dictionary<string, LetDeclaration> letDeclarations)
    {
        var dotIndex = collection.IndexOf('.');
        if (dotIndex < 0) return collection;

        var parentName = collection[..dotIndex];
        var propertyName = collection[(dotIndex + 1)..];

        // Let declarations take priority (local scope: Source.Statements, Disk.Folders)
        if (letDeclarations.ContainsKey(parentName))
            return propertyName;

        // Check if the parent is a known provider namespace (e.g., csharp.Types)
        // If so, keep the qualified name for namespace-aware resolution
        if (_typeRegistry.IsGlobalCollection(collection))
            return collection;

        // Unknown parent — try property name as bare collection for backward compat
        return propertyName;
    }

    /// <summary>
    /// Traces a collection back to its root and returns true if the root is a global collection.
    /// </summary>
    private bool IsGlobalRootCollection(
        string collection,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        HashSet<string>? visited = null)
    {
        // Value-binding dotted access (e.g., "codebase.Types") is always global
        if (IsValueBindingDottedReference(collection, letDeclarations))
            return true;

        collection = ResolveDottedCollection(collection, letDeclarations);

        if (_typeRegistry.IsGlobalCollection(collection))
            return true;

        visited ??= [];
        if (!visited.Add(collection))
            return false;

        if (letDeclarations.TryGetValue(collection, out var letDecl))
        {
            if (letDecl.IsCollectionUnion)
            {
                return ((CollectionUnionExpr)letDecl.ValueExpression!).Elements.All(e =>
                    IsGlobalRootCollection(GetUnionElementName(e), predicateGroups, letDeclarations, new(visited)));
            }
            if (letDecl.IsValueBinding) return true; // Value bindings are document-independent
            return IsGlobalRootCollection(letDecl.BaseCollection, predicateGroups, letDeclarations, visited);
        }

        if (predicateGroups.TryGetValue(collection, out var preds))
            return IsGlobalRootCollection(preds[0].ParameterType, predicateGroups, letDeclarations, visited);

        return false;
    }

    /// <summary>
    /// Resolves a collection from global data (no document dependency).
    /// </summary>
    private List<object> ResolveGlobalCollection(
        string collection,
        PredicateEvaluator evaluator,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<FunctionDefinition>> functionGroups,
        HashSet<string>? visited = null)
    {
        // Dotted value-binding access: codebase.Types, types.Count, types.First
        // Dispatches on RUNTIME TYPE of parent (DataObject, IList, etc.)
        var dottedItems = TryResolveDottedValueBinding(collection, letDeclarations, evaluator);
        if (dottedItems != null) return dottedItems;

        collection = ResolveDottedCollection(collection, letDeclarations);

        visited ??= [];
        if (!visited.Add(collection))
            throw new InvalidOperationException($"Circular collection reference: {collection}");

        // Let declaration (check before global collections to avoid ambiguity on bare names)
        if (letDeclarations.TryGetValue(collection, out var letDecl))
        {
            // Collection union: let Name = a + b + c
            if (letDecl.IsCollectionUnion)
            {
                var unionItems = new List<object>();
                foreach (var elem in ((CollectionUnionExpr)letDecl.ValueExpression!).Elements)
                {
                    var name = GetUnionElementName(elem);
                    var elemItems = ResolveGlobalCollection(name, evaluator, predicateGroups, letDeclarations, functionGroups, new(visited));
                    unionItems.AddRange(elemItems);
                }
                return unionItems;
            }

            if (letDecl.IsValueBinding)
            {
                var value = evaluator.EvaluateField(letDecl.ValueExpression!, null!, "");
                if (value is IList list)
                    return list.Cast<object>().ToList();
                if (value is not null)
                    return [value];
                return [];
            }

            // Path-scoped collection: query provider at specific path
            if (letDecl.PathOverride is not null && _providerQueryService is not null)
            {
                try
                {
                    return ResolvePathScopedCollection(letDecl, evaluator, predicateGroups, functionGroups, useQueryCache: true);
                }
                catch when (letDecl.SourceExpression is not null && letDecl.Filters.Count == 0)
                {
                    // Path-scoped resolution failed — fall through to base collection resolution
                }
            }

            var baseItems = ResolveGlobalCollection(
                letDecl.BaseCollection, evaluator, predicateGroups, letDeclarations, functionGroups, visited);
            var baseItemType = ResolveItemType(letDecl.BaseCollection, predicateGroups, letDeclarations, functionGroups);

            // Fingerprint-based cache for global collections (docPath = null for globals)
            var functionNameSet = functionGroups.Count > 0 ? new HashSet<string>(functionGroups.Keys) : null;
            var fingerprint = QueryFingerprint.Compute(letDecl.BaseCollection, letDecl.Filters, null, functionNameSet, letDecl.PathOverride);
            if (letDecl.Exclusions != null)
                fingerprint += "|!" + QueryFingerprint.Serialize(letDecl.Exclusions);

            if (_queryCache.TryGetValue(fingerprint, out var cached))
                return cached;

            var result = ApplyFilters(baseItems, baseItemType, letDecl.Filters, evaluator, functionGroups);

            if (letDecl.Exclusions != null)
            {
                var finalType = ResolveItemTypeAfterFilters(baseItemType, letDecl.Filters, functionGroups);
                result = ApplyExclusions(result, finalType, letDecl.Exclusions, evaluator, letDeclarations);
            }

            _queryCache.Set(fingerprint, result);
            return result;
        }

        // Direct global collection
        var globalItems = _typeRegistry.GetGlobalCollectionItems(collection);
        if (globalItems is not null)
            return globalItems;

        // Derived from predicate
        if (predicateGroups.TryGetValue(collection, out var preds))
        {
            var pred = preds[0];
            var predBaseItems = ResolveGlobalCollection(
                pred.ParameterType, evaluator, predicateGroups, letDeclarations, functionGroups, visited);
            var predBaseItemType = ResolveItemType(pred.ParameterType, predicateGroups, letDeclarations, functionGroups);

            return predBaseItems.Where(item =>
            {
                var (r, _) = evaluator.EvaluateAsBool(pred.Body, item, predBaseItemType);
                return r;
            }).ToList();
        }

        _diagLog?.Invoke($"[trace] Unknown collection '{collection}'");
        throw new InvalidOperationException($"Unknown collection '{collection}'");
    }

    /// <summary>
    /// Gets the Path property from an item if it has one, for diagnostic location.
    /// </summary>
    private string? GetItemPath(object item)
    {
        var typeName = _typeRegistry.InferTypeName(item);
        if (typeName is not null)
        {
            var pathDesc = _typeRegistry.GetType(typeName)?.GetProperty("Path");
            if (pathDesc?.Accessor is not null)
            {
                var val = pathDesc.Accessor(item);
                if (val is string path) return path;
            }
        }
        return null;
    }

    private string ResolveItemType(
        string collection,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration>? letDeclarations = null,
        Dictionary<string, List<FunctionDefinition>>? functionGroups = null,
        HashSet<string>? visited = null)
    {
        // Resolve dotted collection names (e.g., "Source.Statements" -> "Statements")
        if (letDeclarations != null)
            collection = ResolveDottedCollection(collection, letDeclarations);

        // Check registry for built-in collection -> known item type
        var registryType = _typeRegistry.GetCollectionItemType(collection);
        if (registryType is not null) return registryType;

        // Derived collection — follow chain
        visited ??= [];
        if (!visited.Add(collection))
            return "Unknown";

        if (letDeclarations != null && letDeclarations.TryGetValue(collection, out var letDecl))
        {
            if (letDecl.IsCollectionUnion)
            {
                var firstElem = ((CollectionUnionExpr)letDecl.ValueExpression!).Elements[0];
                return ResolveItemType(GetUnionElementName(firstElem), predicateGroups, letDeclarations, functionGroups, new(visited));
            }
            if (letDecl.IsValueBinding) return "Unknown";
            var baseType = ResolveItemType(letDecl.BaseCollection, predicateGroups, letDeclarations, functionGroups, visited);
            // Follow through any function steps in the filters
            return ResolveItemTypeAfterFilters(baseType, letDecl.Filters, functionGroups);
        }

        if (predicateGroups.TryGetValue(collection, out var preds))
            return ResolveItemType(preds[0].ParameterType, predicateGroups, letDeclarations, functionGroups, visited);

        return "Unknown";
    }

    /// <summary>
    /// Follow filter chain to determine the final item type.
    /// Function steps change the type; predicate steps do not.
    /// </summary>
    private string ResolveItemTypeAfterFilters(
        string baseType, List<Expression> filters,
        Dictionary<string, List<FunctionDefinition>>? functionGroups)
    {
        var currentType = baseType;
        foreach (var filter in filters)
        {
            var funcName = GetFunctionNameFromFilter(filter);
            if (funcName is "Select" or "text")
                currentType = "string";
            else if (funcName != null && functionGroups != null && functionGroups.TryGetValue(funcName, out var group))
                currentType = group[0].ReturnType;
        }
        return currentType;
    }

    /// <summary>
    /// Capture DataObject fields into evaluation context for template resolution.
    /// Allows {TypeName.FieldName} patterns to resolve against function-produced objects.
    /// </summary>
    private static void CaptureAlanObjectFields(EvaluationContext ctx, DataObject ao)
    {
        // Register as a virtual object that responds to property access
        // The TypeName is already captured by the caller; fields are accessed via GetPropertyViaRegistry
    }

    private int GetItemLine(object item)
    {
        var typeName = _typeRegistry.InferTypeName(item);
        if (typeName is not null)
        {
            // Try "Line" property
            var lineDesc = _typeRegistry.GetType(typeName)?.GetProperty("Line");
            if (lineDesc?.Accessor is not null)
            {
                var val = lineDesc.Accessor(item);
                if (val is int lineNum) return lineNum;
            }
            // Try "Number" property (e.g. for Line type)
            var numDesc = _typeRegistry.GetType(typeName)?.GetProperty("Number");
            if (numDesc?.Accessor is not null)
            {
                var val = numDesc.Accessor(item);
                if (val is int num) return num;
            }
        }

        return 0;
    }

    private RichString ResolveTemplate(string template, EvaluationContext context)
    {
        var segments = TemplateParser.Parse(template);
        var spans = new List<TextSpan>();
        foreach (var segment in segments)
        {
            if (segment is LiteralSegment lit)
            {
                spans.Add(new TextSpan(lit.Text));
            }
            else if (segment is AnnotatedLiteralSegment annLit)
            {
                var annotations = RichString.ParseAnnotation(annLit.Annotation);
                spans.Add(new TextSpan(annLit.Text, annotations));
            }
            else if (segment is ExpressionSegment expr)
            {
                var obj = context.Get(expr.PropertyPath[0]);
                if (obj != null)
                {
                    for (int i = 1; i < expr.PropertyPath.Length; i++)
                    {
                        obj = GetPropertyViaRegistry(obj, expr.PropertyPath[i]);
                        if (obj == null) break;
                    }
                }
                var annotations = RichString.ParseAnnotation(expr.Annotation);
                spans.Add(new TextSpan(ConvertToText(obj), annotations));
            }
        }
        return new RichString(spans);
    }

    /// <summary>
    /// Converts an object to its text representation for template rendering.
    /// Uses registered TextConverter if available, otherwise falls back to ToString().
    /// </summary>
    private string ConvertToText(object? obj)
    {
        if (obj is null) return "";
        if (obj is string s) return s;
        if (obj is DataObject so) return so.ToJson();
        var typeName = _typeRegistry.InferTypeName(obj);
        if (typeName is not null)
        {
            var typeDesc = _typeRegistry.GetType(typeName);
            if (typeDesc?.TextConverter is not null)
                return typeDesc.TextConverter(obj);
        }
        return obj.ToString() ?? "";
    }

    private object? GetPropertyViaRegistry(object obj, string property)
    {
        // DataObject: resolve fields by name (includes lazy resolver)
        if (obj is DataObject ao)
            return ao.GetField(property);

        var typeName = _typeRegistry.InferTypeName(obj);
        if (typeName is not null)
        {
            var desc = _typeRegistry.GetType(typeName)?.GetProperty(property);
            if (desc?.Accessor is not null)
            {
                var value = desc.Accessor(obj);
                return value;
            }
        }

        // Fallback for IList (Count), string (Length)
        if (obj is System.Collections.IList list)
        {
            return property switch
            {
                "Count" => list.Count,
                _ => null
            };
        }

        return obj switch
        {
            string s => property switch { "Length" => s.Length.ToString(), _ => null },
            _ => null
        };
    }

    private bool EvaluateGuard(
        Expression guard,
        ProgramInfo program,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<FunctionDefinition>> functionGroups)
    {
        // Create a PredicateEvaluator with Program as the item
        var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
        var (result, _) = evaluator.EvaluateAsBool(guard, program, "Program");
        return result;
    }

    private Dictionary<string, int> ComputeAggregateCounts(List<Document> documents)
    {
        var counts = new Dictionary<string, int>();
        foreach (var doc in documents)
        {
            foreach (var collName in _typeRegistry.GetDocumentCollectionNames())
            {
                var items = _typeRegistry.GetCollectionItems(collName, doc);
                if (items is not null)
                {
                    counts.TryGetValue(collName, out var current);
                    counts[collName] = current + items.Count;
                }
            }
        }

        // Include global collections (all providers)
        foreach (var collName in _typeRegistry.GetGlobalCollectionNames())
        {
            var items = _typeRegistry.GetGlobalCollectionItems(collName);
            if (items is not null)
                counts[collName] = items.Count;
        }

        return counts;
    }

    /// <summary>
    /// Follow let-declaration chains to find the root collection name(s).
    /// For unions (a + b + c), returns all root collections.
    /// e.g., "public-types" -> let public-types = Types:isPublic -> ["Types"]
    /// e.g., "all" -> let all = a + b -> resolves each branch recursively
    /// </summary>
    private static List<string> ResolveRootCollections(string name, Dictionary<string, LetDeclaration> letDeclarations)
    {
        var results = new List<string>();
        ResolveRootCollectionsRecursive(name, letDeclarations, results, []);
        return results;
    }

    private static void ResolveRootCollectionsRecursive(string name, Dictionary<string, LetDeclaration> letDeclarations, List<string> results, HashSet<string> visited)
    {
        if (!visited.Add(name)) return;

        if (!letDeclarations.TryGetValue(name, out var let))
        {
            // Not a let declaration — this is a root provider collection
            if (!string.IsNullOrEmpty(name))
                results.Add(name);
            return;
        }

        // Union: recurse into each element
        if (let.IsCollectionUnion && let.ValueExpression is CollectionUnionExpr union)
        {
            foreach (var elem in union.Elements)
            {
                ResolveRootCollectionsRecursive(GetUnionElementName(elem), letDeclarations, results, visited);
            }
            return;
        }

        // Regular let chain: follow BaseCollection
        if (!string.IsNullOrEmpty(let.BaseCollection))
            ResolveRootCollectionsRecursive(let.BaseCollection, letDeclarations, results, visited);
    }

    /// <summary>
    /// Pre-resolve let declarations that use .Select() across ALL documents.
    /// These produce string lists used for cross-document comparisons (e.g., API compat).
    /// </summary>
    private Dictionary<string, IList>? PreResolveGlobalSelects(
        Dictionary<string, LetDeclaration> letDeclarations,
        List<Document> documents,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, List<FunctionDefinition>> functionGroups)
    {
        // Find let declarations that use .Select()
        var selectLets = letDeclarations
            .Where(kv => !kv.Value.IsValueBinding && !kv.Value.IsCollectionUnion &&
                         kv.Value.Filters.Any(f => f is CallExpr fc && fc.Name == "Select"))
            .ToList();

        if (selectLets.Count == 0) return null;

        // Aggregate per-document collection items
        var aggregated = new Dictionary<string, List<object>>();
        foreach (var collName in _typeRegistry.GetDocumentCollectionNames())
        {
            var allItems = new List<object>();
            foreach (var doc in documents)
            {
                var items = _typeRegistry.GetCollectionItems(collName, doc);
                if (items is not null)
                    allItems.AddRange(items);
            }
            if (allItems.Count > 0)
                aggregated[collName] = allItems;
        }

        // Temporarily register aggregated collections as global for resolution
        var previousGlobals = new Dictionary<string, List<object>?>();
        foreach (var (name, items) in aggregated)
        {
            previousGlobals[name] = _typeRegistry.GetGlobalCollectionItems(name);
            _typeRegistry.RegisterGlobalCollection(name, items);
        }

        try
        {
            var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
            var resolved = new Dictionary<string, IList>();

            foreach (var (name, letDecl) in selectLets)
            {
                try
                {
                    var items = ResolveGlobalCollection(name, evaluator, predicateGroups, letDeclarations, functionGroups);
                    resolved[name] = items;
                }
                catch
                {
                    // Skip if resolution fails
                }
            }

            return resolved.Count > 0 ? resolved : null;
        }
        finally
        {
            // Restore previous global collection state
            foreach (var (name, prev) in previousGlobals)
            {
                if (prev is not null)
                    _typeRegistry.RegisterGlobalCollection(name, prev);
                else
                    _typeRegistry.UnregisterGlobalCollection(name);
            }
        }
    }

    private RichString ResolveAggregateTemplate(string template, Dictionary<string, int> aggregateCounts)
    {
        var segments = TemplateParser.Parse(template);
        var spans = new List<TextSpan>();
        foreach (var segment in segments)
        {
            if (segment is LiteralSegment lit)
            {
                spans.Add(new TextSpan(lit.Text));
            }
            else if (segment is AnnotatedLiteralSegment annLit)
            {
                var annotations = RichString.ParseAnnotation(annLit.Annotation);
                spans.Add(new TextSpan(annLit.Text, annotations));
            }
            else if (segment is ExpressionSegment expr && expr.PropertyPath.Length >= 2)
            {
                var lastProp = expr.PropertyPath[^1];
                var collName = expr.PropertyPath[^2];
                if (lastProp == "Count" && aggregateCounts.TryGetValue(collName, out var count))
                {
                    var annotations = RichString.ParseAnnotation(expr.Annotation);
                    spans.Add(new TextSpan(count.ToString(), annotations));
                }
            }
        }
        return new RichString(spans);
    }

    /// Expand a command block: if it's a command reference, replace it with the referenced blocks.
    /// Guards from the referencing block are applied to each expanded block.
    private void ExpandCommandRef(
        CommandBlock cmd,
        Dictionary<string, List<CommandBlock>> allCommands,
        List<CommandBlock> result,
        HashSet<string> activeStack)
    {
        if (cmd.CommandRef is null)
        {
            result.Add(cmd);
            return;
        }

        if (!activeStack.Add(cmd.CommandRef))
            return; // cycle — skip

        if (allCommands.TryGetValue(cmd.CommandRef, out var refBlocks))
        {
            foreach (var refBlock in refBlocks)
            {
                // Clone with the caller's name and combine guards
                var expanded = refBlock with { Name = cmd.Name };
                if (cmd.Guard is not null)
                    expanded = expanded with { Guard = cmd.Guard };
                ExpandCommandRef(expanded, allCommands, result, activeStack);
            }
        }

        activeStack.Remove(cmd.CommandRef);
    }

    private SinkProvider ResolveSink(SinkTarget target, Dictionary<string, IList>? resolvedCollections = null)
    {
        var sink = _typeRegistry.ResolveSink(target.Name);
        if (sink is null && resolvedCollections is not null)
        {
            // Fall back: if the target name matches a resolved collection (let-binding list),
            // wrap it with ListAppendSink — pipe enqueues items into the list.
            if (resolvedCollections.TryGetValue(target.Name, out var list))
                sink = new ListAppendSink(list);
        }
        if (sink is null)
            throw new InvalidOperationException($"Sink '{target.Name}' not found. Use a qualified name like 'console.WriteLine' or 'file.Write'.");
        if (target.Args is { Count: > 0 })
            sink = sink.WithArgs(target.Args);
        return sink;
    }

    /// <summary>
    /// Check if a command block involves a specific function call (by OutputExpression).
    /// Used to identify commands like assert(...), save(...) etc. for filtering.
    /// </summary>
    private static bool IsCallTo(CommandBlock cmd, string functionName)
    {
        return cmd.OutputExpression is CallExpr call && call.Name == functionName;
    }

    /// <summary>
    /// Checks if a command is a parameterized command invocation (e.g., CHECK(var-usage))
    /// whose argument name matches the commandFilter.
    /// </summary>
    private static bool MatchesCommandFilter(CommandBlock cmd, HashSet<string> filter, Dictionary<string, List<CommandBlock>> allCommands)
    {
        if (cmd.OutputExpression is not CallExpr call) return false;
        if (!allCommands.TryGetValue(call.Name, out var targets)) return false;
        if (targets.Count == 0 || targets[0].Parameters is not { Count: > 0 }) return false;
        // Check if any argument identifier matches the filter
        return call.Args.Any(a => a is IdentifierExpr id && filter.Contains(id.Name));
    }

    /// <summary>
    /// Execute an ASSERT command: evaluate a boolean condition expression and record pass/fail.
    /// </summary>
    private void ExecuteAssert(
        CommandBlock cmd,
        List<Document> documents,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<FunctionDefinition>> functionGroups,
        List<AssertResult> allAsserts)
    {
        var evaluator = CreateEvaluator(predicateGroups, "", letDeclarations, functionGroups);
        bool passed;

        if (cmd.OutputExpression is not null)
        {
            // New form: ASSERT(boolExpr, 'description')
            var value = evaluator.EvaluateField(cmd.OutputExpression, null!, "");
            passed = PredicateEvaluator.ToBool(value);
        }
        else if (cmd.Collection is not null)
        {
            // Legacy form: ASSERT(collection:filters, 'description') — non-empty check
            string itemType = ResolveItemType(cmd.Collection, predicateGroups, letDeclarations, functionGroups);
            List<object> items;
            if (IsGlobalRootCollection(cmd.Collection, predicateGroups, letDeclarations))
            {
                items = ResolveGlobalCollection(cmd.Collection, evaluator, predicateGroups, letDeclarations, functionGroups);
                items = ApplyFilters(items, itemType, cmd.Filters, evaluator, functionGroups);
                if (cmd.Exclusions != null)
                {
                    string finalItemType = ResolveItemTypeAfterFilters(itemType, cmd.Filters, functionGroups);
                    items = ApplyExclusions(items, finalItemType, cmd.Exclusions, evaluator, letDeclarations);
                }
            }
            else
            {
                items = [];
            }
            passed = items.Count > 0;
        }
        else
        {
            passed = false;
        }

        string message = !string.IsNullOrEmpty(cmd.MessageTemplate)
            ? cmd.MessageTemplate
            : $"{cmd.Name}: expected true";

        allAsserts.Add(new AssertResult(cmd.Name, passed, message, 0));
    }

    /// <summary>
    /// Write save/SAVE output to file. Handles two patterns:
    /// - SAVE('path', '{template}', collection): OutputPath is set, richMessage is the formatted template
    /// - save('path', value): OutputPath is null, MessageTemplate is the path, item is the content
    /// </summary>
    private static void WriteSaveOutput(
        CommandBlock cmd, RichString richMessage, object? item,
        Dictionary<string, List<string>> fileOutputs)
    {
        string path;
        string content;

        if (cmd.OutputPath is not null)
        {
            // Legacy SAVE('path', '{template}', collection) — format each item with template
            path = cmd.OutputPath;
            content = richMessage.ToPlainText();
        }
        else
        {
            // New save('path', value) — messageTemplate is the path, item is the content
            path = cmd.MessageTemplate;
            content = item is string s ? s : item?.ToString() ?? "";
        }

        if (!fileOutputs.TryGetValue(path, out var lines))
        {
            lines = [];
            fileOutputs[path] = lines;
        }
        lines.Add(content);
    }

    /// <summary>
    /// Checks if a collection name is a dotted reference to a value-binding let (e.g., "codebase.Types").
    /// Only returns true for explicit value bindings — SourceExpression fallbacks are handled
    /// at resolution time by TryResolveDottedValueBinding (with try/catch for ambiguous cases).
    /// </summary>
    private static bool IsValueBindingDottedReference(string collection, Dictionary<string, LetDeclaration> letDeclarations)
    {
        var dotIndex = collection.IndexOf('.');
        if (dotIndex < 0) return false;

        var parentName = collection[..dotIndex];
        if (!letDeclarations.TryGetValue(parentName, out var letDecl)) return false;
        // Value bindings OR lets with SourceExpression (e.g., provider function calls
        // that the parser couldn't distinguish from path-scoped collections)
        return letDecl.IsValueBinding || letDecl.SourceExpression is not null;
    }

    /// <summary>
    /// Resolves a dotted collection reference where the parent is a let-bound value.
    /// Uses the evaluator for member access — DataObject's lazy field resolver
    /// handles Code() objects, IList handles .Count/.First, etc.
    /// e.g., "codebase.Types" where codebase is a DataObject with lazy fields,
    ///        "types.First" where types evaluates to IList.
    /// </summary>
    private List<object>? TryResolveDottedValueBinding(
        string collection,
        Dictionary<string, LetDeclaration> letDeclarations,
        PredicateEvaluator evaluator)
    {
        var dotIndex = collection.IndexOf('.');
        if (dotIndex < 0) return null;

        var parentName = collection[..dotIndex];
        var memberName = collection[(dotIndex + 1)..];

        if (!letDeclarations.TryGetValue(parentName, out var letDecl))
            return null;

        // Get the parent's evaluable expression (value binding or SourceExpression fallback)
        var parentExpr = letDecl.IsValueBinding ? letDecl.ValueExpression
                       : letDecl.SourceExpression;
        if (parentExpr is null) return null;

        // Evaluate parent.member through the evaluator's member access dispatch
        // Use IdentifierExpr(parentName) so the let binding's type annotation is applied
        var memberExpr = new MemberAccessExpr(new IdentifierExpr(parentName), memberName);
        try
        {
            var memberResult = evaluator.EvaluateField(memberExpr, null!, "");
            if (memberResult is IList<object> typedList) return typedList.ToList();
            if (memberResult is IList list) return list.Cast<object>().ToList();
            if (memberResult is not null) return [memberResult];
            return [];
        }
        catch
        {
            // For real value bindings (Code(), csharp.Code(), etc.), propagate errors
            // so user sees meaningful messages like "provider not imported"
            if (letDecl.IsValueBinding) throw;
            // For SourceExpression fallbacks (speculative), silently fall through
            return null;
        }
    }
}
