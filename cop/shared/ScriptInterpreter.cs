using System.Collections;
using System.Diagnostics;

namespace Cop.Lang;

public class ScriptInterpreter
{
    private readonly TypeRegistry _typeRegistry;
    private readonly int _maxOutputsPerCommand;
    private readonly TimeSpan _timeout;
    private Dictionary<string, IList>? _globalResolvedSelects;
    private Dictionary<string, List<Document>>? _loadDocuments;

    // Per-document cache for resolved let bindings — shared across all commands
    private readonly Dictionary<string, Dictionary<string, IList>> _documentLetCache = new();
    // Fingerprint-based cache for resolved filtered collections (order-independent).
    // Bounded to prevent unbounded memory growth on large repos with many unique queries.
    private readonly BoundedCache<string, List<object>> _queryCache = new(capacity: 2048);

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

    public ScriptInterpreter(
        TypeRegistry typeRegistry,
        int maxOutputsPerCommand = 1000,
        TimeSpan? timeout = null)
    {
        _typeRegistry = typeRegistry;
        _maxOutputsPerCommand = maxOutputsPerCommand;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
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

        // Create Program built-in
        var program = new ProgramInfo(new List<string>(programArgs ?? []));

        // Build predicate dictionary across all script files (grouped by name for overloading)
        var predicateGroups = new Dictionary<string, List<PredicateDefinition>>();
        foreach (var ScriptFile in scriptFiles)
        {
            foreach (var pred in ScriptFile.Predicates)
            {
                if (!predicateGroups.TryGetValue(pred.Name, out var group))
                {
                    group = [];
                    predicateGroups[pred.Name] = group;
                }
                group.Add(pred);
            }
        }

        // Build function dictionary across all script files (grouped by name)
        var functionGroups = new Dictionary<string, List<FunctionDefinition>>();
        foreach (var ScriptFile in scriptFiles)
        {
            foreach (var func in ScriptFile.Functions)
            {
                if (!functionGroups.TryGetValue(func.Name, out var group))
                {
                    group = [];
                    functionGroups[func.Name] = group;
                }
                group.Add(func);
            }
        }

        // Build let declaration dictionary across all script files
        var letDeclarations = new Dictionary<string, LetDeclaration>();
        foreach (var ScriptFile in scriptFiles)
        {
            foreach (var let in ScriptFile.LetDeclarations)
                letDeclarations[let.Name] = let;
        }

        // Compute aggregate collection counts
        var aggregateCounts = ComputeAggregateCounts(documents);

        // Pre-resolve let declarations that use .Select() — these need global (cross-document) data
        _globalResolvedSelects = PreResolveGlobalSelects(
            letDeclarations, documents, predicateGroups, functionGroups);

        // Build command lookup table across all script files for expanding refs
        var allCommands = new Dictionary<string, List<CommandBlock>>(StringComparer.OrdinalIgnoreCase);
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
                commandsToRun = ScriptFile.Commands.Where(c => IsAssertAction(c.ActionName));
            }
            else if (commandName != null)
            {
                // Run only matching named commands (legacy single-command mode)
                commandsToRun = ScriptFile.Commands.Where(c => c.IsCommand && string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));
            }
            else if (commandFilter != null)
            {
                // Run only commands whose name matches the filter (supports auto-derived names)
                commandsToRun = ScriptFile.Commands.Where(c =>
                    !IsSaveAction(c.ActionName) && !IsAssertAction(c.ActionName) &&
                    commandFilter.Contains(c.Name));
            }
            else
            {
                // Run all commands but skip SAVE and ASSERT actions (require explicit invocation)
                commandsToRun = ScriptFile.Commands.Where(c => !IsSaveAction(c.ActionName) && !IsAssertAction(c.ActionName));
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

                // If ActionName matches a parameterized command, resolve as invocation
                // e.g., CHECK(console-calls:notTest) → bind console-calls:notTest to CHECK's parameter
                if (cmd.ActionName != null
                    && allCommands.TryGetValue(cmd.ActionName, out var targetCmds)
                    && targetCmds.Count > 0
                    && targetCmds[0].Parameters is { Count: > 0 })
                {
                    var target = targetCmds[0];
                    var tempLets = new Dictionary<string, LetDeclaration>(letDeclarations);
                    if (cmd.Collection != null && target.Parameters.Count > 0)
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
                    // → let violations = var-usage
                    var tempLets = new Dictionary<string, LetDeclaration>(letDeclarations);
                    for (int i = 0; i < Math.Min(cmdTemplate.Parameters.Count, run.Arguments.Count); i++)
                    {
                        var paramName = cmdTemplate.Parameters[i];
                        var argExpr = run.Arguments[i];
                        var (collection, filters, exclusions) = ScriptParser.DecomposeCollectionExpression(argExpr);
                        tempLets[paramName] = new LetDeclaration(paramName, collection, filters, run.Line)
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

        outputs = fileOutputs.Select(kv =>
            new FileOutput(kv.Key, string.Join(Environment.NewLine, kv.Value)))
            .ToList();

        // Warn about empty root collections referenced by executed commands
        // Skip warning in assert mode — test results are the intended output
        var warnings = new List<string>();
        if (!assertMode && allOutputs.Count == 0 && outputs.Count == 0)
        {
            // Only check collections from commands that actually executed (not all commands in all files)
            var referencedCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sf in scriptFiles)
            {
                IEnumerable<CommandBlock> executed;
                if (commandName != null)
                    executed = sf.Commands.Where(c => c.IsCommand && string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));
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
            var emptyRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        // ASSERT / ASSERT_EMPTY: resolve collection, count items, record result
        if (IsAssertAction(cmd.ActionName) && cmd.Collection is not null)
        {
            ExecuteAssert(cmd, documents, predicateGroups, letDeclarations, functionGroups, allAsserts);
            return;
        }

        // Bare command — no collection, execute once
        if (cmd.Collection is null)
        {
            var richMessage = ResolveAggregateTemplate(cmd.MessageTemplate, aggregateCounts);

            if (IsSaveAction(cmd.ActionName) && cmd.OutputPath is not null)
            {
                WriteSaveOutput(cmd, richMessage, null, fileOutputs);
            }
            else
            {
                allOutputs.Add(new PrintOutput(richMessage));
            }
            return;
        }

        var sw = Stopwatch.StartNew();
        int count = 0;

        string itemType = ResolveItemType(cmd.Collection, predicateGroups, letDeclarations, functionGroups);
        string finalItemType = ResolveItemTypeAfterFilters(itemType, cmd.Filters, functionGroups);

        // Global collections are processed once (not per-source-file)
        if (IsGlobalRootCollection(cmd.Collection, predicateGroups, letDeclarations))
        {
            var evaluator = new PredicateEvaluator(predicateGroups, "", _typeRegistry, letDeclarations, functionGroups);
            var items = ResolveGlobalCollection(cmd.Collection, evaluator, predicateGroups, letDeclarations, functionGroups);
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
                if (item is ScriptObject ao)
                    CaptureAlanObjectFields(finalCtx, ao);

                var richMessage = ResolveTemplate(cmd.MessageTemplate, finalCtx);
                if (IsSaveAction(cmd.ActionName))
                {
                    WriteSaveOutput(cmd, richMessage, item, fileOutputs);
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

            var evaluator = new PredicateEvaluator(predicateGroups, document.Path, _typeRegistry,
                letDeclarations, functionGroups, resolvedCollections);
            var items = ResolveCollection(cmd.Collection, document, evaluator, predicateGroups, letDeclarations, functionGroups);
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
                if (item is ScriptObject ao)
                    CaptureAlanObjectFields(finalCtx, ao);

                var richMessage = ResolveTemplate(cmd.MessageTemplate, finalCtx);
                if (IsSaveAction(cmd.ActionName))
                {
                    WriteSaveOutput(cmd, richMessage, item, fileOutputs);
                }
                else
                {
                    allOutputs.Add(new PrintOutput(richMessage));
                }
                count++;
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
        // Load() dotted access (e.g., "dll.Api", "dll.Types") — resolve from loaded documents
        var loadItems = TryResolveLoadCollection(collection, letDeclarations);
        if (loadItems != null) return loadItems;

        // Resolve dotted collection names (e.g., "Source.Statements" → "Statements")
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

            // Bare Load() reference without sub-collection — not valid as a collection
            if (letDecl.IsExternalLoad)
            {
                throw new InvalidOperationException(
                    $"'{collection}' is a Load() binding. Use '{collection}.Types', '{collection}.Api', etc. to access sub-collections.");
            }

            // Parse('file.json', [Type]) — resolve to a flat typed collection
            if (letDecl.IsFileParse)
            {
                var parsed = ResolveFileParse(letDecl);
                _typeRegistry.RegisterGlobalCollection(collection, parsed);
                return parsed;
            }

            // Value bindings (let Name = [...]) are not collections — skip
            if (letDecl.IsValueBinding)
                throw new InvalidOperationException($"'{collection}' is a value binding, not a collection");

            var baseItems = ResolveCollection(
                letDecl.BaseCollection, document, evaluator, predicateGroups, letDeclarations, functionGroups, visited, useQueryCache);
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
                var fingerprint = QueryFingerprint.Compute(letDecl.BaseCollection, letDecl.Filters, document.Path, functionNameSet);
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
        var bootstrapEvaluator = new PredicateEvaluator(
            predicateGroups, document.Path, _typeRegistry, letDeclarations, functionGroups);

        foreach (var (name, letDecl) in letDeclarations)
        {
            // Skip value bindings, collection unions, Load(), and Parse() — handled elsewhere
            if (letDecl.IsExternalLoad) continue;
            if (letDecl.IsFileParse) continue;
            if (letDecl.IsValueBinding || letDecl.IsCollectionUnion) continue;
            // Skip check-level lets (those with actions like :toWarning) — they are commands, not data
            if (letDecl.Filters.Any(f =>
                (f is FunctionCallExpr fc && IsActionFilter(fc.Name)) ||
                (f is PredicateCallExpr pc && IsActionFilter(pc.Name)))) continue;

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
        name is "toError" or "toWarning" or "toInfo" or "toOutput" or "toSave";

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

        foreach (var filter in filters)
        {
            // Detect if this filter is a function call
            var funcName = GetFunctionNameFromFilter(filter);

            // Handle .Select() — project each item to a string value
            if (funcName == "Select")
            {
                var fieldArgs = GetFilterArgs(filter);
                if (fieldArgs.Count > 0)
                {
                    // Barrier: materialize before projection (type changes)
                    var materialized = current.Where(item => item is not null).ToList();
                    current = materialized.Select(item =>
                    {
                        var value = evaluator.EvaluateField(fieldArgs[0], item, currentType);
                        return (object)(value?.ToString() ?? "");
                    }).ToList();
                    currentType = "string";
                    continue;
                }
            }
            // Handle :text() — format each item with a template, join into a single string
            else if (funcName == "Text")
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
                            if (item is ScriptObject ao)
                                CaptureAlanObjectFields(ctx, ao);
                            return ResolveTemplate(template, ctx).ToPlainText();
                        })
                        .ToList();
                    current = [(object)string.Join(Environment.NewLine, lines)];
                    currentType = "string";
                    continue;
                }
            }
            else if (funcName != null && functionGroups.ContainsKey(funcName))
            {
                // Barrier: function map transforms items (type changes)
                var funcArgs = GetFilterArgs(filter);
                var capturedType = currentType;
                current = current.Select(item =>
                    (object)evaluator.ApplyFunction(funcName, item, capturedType, funcArgs)).ToList();
                currentType = evaluator.GetFunctionReturnType(funcName) ?? currentType;
            }
            else
            {
                // Predicate filter: compose lazily — no materialization
                var capturedType = currentType;
                var capturedFilter = filter;
                current = current.Where(item =>
                {
                    var (result, _) = evaluator.EvaluateAsBool(capturedFilter, item, capturedType);
                    return result;
                });
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
        if (item is ScriptObject ao)
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
            PredicateCallExpr pc => pc.Name,
            FunctionCallExpr fc => fc.Name,
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
            PredicateCallExpr pc => pc.Args,
            FunctionCallExpr fc => fc.Args,
            _ => []
        };
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
        // Load() dotted access (e.g., "dll.Api") is always global
        if (IsLoadDottedReference(collection, letDeclarations))
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
            if (letDecl.IsExternalLoad) return true; // Load() is self-contained, process once globally
            if (letDecl.IsFileParse) return true; // Parse() is self-contained, process once globally
            if (letDecl.IsValueBinding) return false;
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
        // Load() dotted access (e.g., "dll.Api", "dll.Types") — resolve from loaded documents
        var loadItems = TryResolveLoadCollection(collection, letDeclarations);
        if (loadItems != null) return loadItems;

        collection = ResolveDottedCollection(collection, letDeclarations);

        // Direct global collection
        var globalItems = _typeRegistry.GetGlobalCollectionItems(collection);
        if (globalItems is not null)
            return globalItems;

        visited ??= [];
        if (!visited.Add(collection))
            throw new InvalidOperationException($"Circular collection reference: {collection}");

        // Let declaration
        if (letDeclarations.TryGetValue(collection, out var letDecl))
        {
            // Collection union: let Name = a + b + c
            if (letDecl.IsCollectionUnion)
            {
                var unionItems = new List<object>();
                foreach (var elem in ((CollectionUnionExpr)letDecl.ValueExpression!).Elements)
                {
                    var name = GetUnionElementName(elem);
                    unionItems.AddRange(ResolveGlobalCollection(name, evaluator, predicateGroups, letDeclarations, functionGroups, new(visited)));
                }
                return unionItems;
            }

            // Bare Load() reference without sub-collection — not valid as a collection
            if (letDecl.IsExternalLoad)
            {
                throw new InvalidOperationException(
                    $"'{collection}' is a Load() binding. Use '{collection}.Types', '{collection}.Api', etc. to access sub-collections.");
            }

            // Parse('file.json', [Type]) — resolve to a flat typed collection
            if (letDecl.IsFileParse)
            {
                var items = ResolveFileParse(letDecl);
                _typeRegistry.RegisterGlobalCollection(collection, items);
                return items;
            }

            if (letDecl.IsValueBinding)
                throw new InvalidOperationException($"'{collection}' is a value binding, not a collection");

            var baseItems = ResolveGlobalCollection(
                letDecl.BaseCollection, evaluator, predicateGroups, letDeclarations, functionGroups, visited);
            var baseItemType = ResolveItemType(letDecl.BaseCollection, predicateGroups, letDeclarations, functionGroups);

            // Fingerprint-based cache for global collections (docPath = null for globals)
            var functionNameSet = functionGroups.Count > 0 ? new HashSet<string>(functionGroups.Keys) : null;
            var fingerprint = QueryFingerprint.Compute(letDecl.BaseCollection, letDecl.Filters, null, functionNameSet);
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

        // Derived from predicate
        if (predicateGroups.TryGetValue(collection, out var preds))
        {
            var pred = preds[0];
            var baseItems = ResolveGlobalCollection(
                pred.ParameterType, evaluator, predicateGroups, letDeclarations, functionGroups, visited);
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
        // Load() dotted access (e.g., "dll.Api") — resolve sub-collection type from registry
        if (letDeclarations != null && IsLoadDottedReference(collection, letDeclarations))
        {
            var subCollectionName = collection[(collection.IndexOf('.') + 1)..];
            return _typeRegistry.GetCollectionItemType(subCollectionName) ?? "Unknown";
        }

        // Resolve dotted collection names (e.g., "Source.Statements" → "Statements")
        if (letDeclarations != null)
            collection = ResolveDottedCollection(collection, letDeclarations);

        // Check registry for built-in collection → known item type
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
            if (letDecl.IsExternalLoad) return "Unknown"; // bare Load() — no sub-collection
            if (letDecl.IsFileParse) return ExtractParseTypeName(letDecl); // type from Parse args
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
            if (funcName is "Select" or "Text")
                currentType = "string";
            else if (funcName != null && functionGroups != null && functionGroups.TryGetValue(funcName, out var group))
                currentType = group[0].ReturnType;
        }
        return currentType;
    }

    /// <summary>
    /// Capture ScriptObject fields into evaluation context for template resolution.
    /// Allows {TypeName.FieldName} patterns to resolve against function-produced objects.
    /// </summary>
    private static void CaptureAlanObjectFields(EvaluationContext ctx, ScriptObject ao)
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
                spans.Add(new TextSpan(obj?.ToString() ?? "", annotations));
            }
        }
        return new RichString(spans);
    }

    private object? GetPropertyViaRegistry(object obj, string property)
    {
        // ScriptObject: resolve fields by name
        if (obj is ScriptObject ao)
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
        var evaluator = new PredicateEvaluator(predicateGroups, "", _typeRegistry, letDeclarations, functionGroups);
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
    /// e.g., "public-types" → let public-types = Types:isPublic → ["Types"]
    /// e.g., "all" → let all = a + b → resolves each branch recursively
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
                         kv.Value.Filters.Any(f => f is FunctionCallExpr fc && fc.Name == "Select"))
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
            var evaluator = new PredicateEvaluator(predicateGroups, "", _typeRegistry, letDeclarations, functionGroups);
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

    private static bool IsSaveAction(string? actionName) =>
        string.Equals(actionName, "SAVE", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssertAction(string? actionName) =>
        string.Equals(actionName, "ASSERT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(actionName, "ASSERT_EMPTY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Execute an ASSERT or ASSERT_EMPTY command: resolve collection, count items, record pass/fail.
    /// </summary>
    private void ExecuteAssert(
        CommandBlock cmd,
        List<Document> documents,
        Dictionary<string, List<PredicateDefinition>> predicateGroups,
        Dictionary<string, LetDeclaration> letDeclarations,
        Dictionary<string, List<FunctionDefinition>> functionGroups,
        List<AssertResult> allAsserts)
    {
        bool expectEmpty = string.Equals(cmd.ActionName, "ASSERT_EMPTY", StringComparison.OrdinalIgnoreCase);
        string itemType = ResolveItemType(cmd.Collection!, predicateGroups, letDeclarations, functionGroups);

        List<object> items;
        if (IsGlobalRootCollection(cmd.Collection!, predicateGroups, letDeclarations))
        {
            var evaluator = new PredicateEvaluator(predicateGroups, "", _typeRegistry, letDeclarations, functionGroups);
            items = ResolveGlobalCollection(cmd.Collection!, evaluator, predicateGroups, letDeclarations, functionGroups);
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

        int count = items.Count;
        bool passed = expectEmpty ? count == 0 : count > 0;
        string message = !string.IsNullOrEmpty(cmd.MessageTemplate)
            ? cmd.MessageTemplate
            : expectEmpty
                ? $"{cmd.Name}: expected empty"
                : $"{cmd.Name}: expected non-empty";

        allAsserts.Add(new AssertResult(cmd.Name, passed, message, count));
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
    /// Checks if a collection name is a dotted reference to a Load() let (e.g., "dll.Api", "dll.Types").
    /// </summary>
    private static bool IsLoadDottedReference(string collection, Dictionary<string, LetDeclaration> letDeclarations)
    {
        var dotIndex = collection.IndexOf('.');
        if (dotIndex < 0) return false;

        var parentName = collection[..dotIndex];
        return letDeclarations.TryGetValue(parentName, out var letDecl) && letDecl.IsExternalLoad;
    }

    /// <summary>
    /// Resolves a Load() dotted collection (e.g., "dll.Api") by extracting the sub-collection
    /// from the loaded documents using the same collection extractors as document loading.
    /// </summary>
    private List<object>? TryResolveLoadCollection(
        string collection,
        Dictionary<string, LetDeclaration> letDeclarations)
    {
        var dotIndex = collection.IndexOf('.');
        if (dotIndex < 0) return null;

        var parentName = collection[..dotIndex];
        var subCollectionName = collection[(dotIndex + 1)..];

        if (!letDeclarations.TryGetValue(parentName, out var letDecl) || !letDecl.IsExternalLoad)
            return null;

        var docs = ResolveLoadDocuments(letDecl);
        var items = new List<object>();
        foreach (var doc in docs)
        {
            var extracted = _typeRegistry.GetCollectionItems(subCollectionName, doc);
            if (extracted != null)
                items.AddRange(extracted);
        }
        return items;
    }

    /// <summary>
    /// Resolves and caches Load() documents. Keyed by resolved path for dedup.
    /// </summary>
    private List<Document> ResolveLoadDocuments(LetDeclaration letDecl)
    {
        _loadDocuments ??= new();
        var path = ExtractLoadPath(letDecl);
        if (!_loadDocuments.TryGetValue(path, out var docs))
        {
            docs = ResolveLoad(letDecl);
            _loadDocuments[path] = docs;
        }
        return docs;
    }

    /// <summary>
    /// Resolve a Load('path') let declaration by loading external documents (DLL or source files).
    /// Returns documents wrapping SourceFiles — the same model as implicit source loading.
    /// </summary>
    private List<Document> ResolveLoad(LetDeclaration letDecl)
    {
        var loader = _typeRegistry.DocumentLoader
            ?? throw new InvalidOperationException("Load() is not available — no document loader registered");

        var path = ExtractLoadPath(letDecl);
        return loader(path);
    }

    /// <summary>
    /// Extracts the path string from a Load('path') let declaration.
    /// </summary>
    private static string ExtractLoadPath(LetDeclaration letDecl)
    {
        var loadExpr = (FunctionCallExpr)letDecl.ValueExpression!;
        if (loadExpr.Args.Count == 0)
            throw new InvalidOperationException("Load() requires a path argument");

        var pathArg = loadExpr.Args[0];
        if (pathArg is LiteralExpr lit && lit.Value is string s)
            return s;

        throw new InvalidOperationException("Load() path argument must be a string literal");
    }

    /// <summary>
    /// Resolves a Parse('file.ext', [Type]) let declaration by delegating to a
    /// registered file parser for the file's extension.
    /// </summary>
    private List<object> ResolveFileParse(LetDeclaration letDecl)
    {
        var (filePath, typeName) = ExtractParseArgs(letDecl);
        var items = _typeRegistry.TryParseFile(filePath, typeName);
        if (items == null)
        {
            var ext = Path.GetExtension(filePath);
            throw new InvalidOperationException(
                $"No file parser registered for '{ext}' files. Parse() cannot handle '{filePath}'.");
        }
        return items;
    }

    /// <summary>
    /// Extracts the file path and type name from a Parse('file.json', [Type]) let declaration.
    /// </summary>
    private static (string FilePath, string TypeName) ExtractParseArgs(LetDeclaration letDecl)
    {
        var parseExpr = (FunctionCallExpr)letDecl.ValueExpression!;

        if (parseExpr.Args.Count < 2)
            throw new InvalidOperationException(
                $"Parse() requires two arguments: Parse('file.json', [TypeName]). Got {parseExpr.Args.Count} argument(s).");

        // First arg: file path (string literal)
        if (parseExpr.Args[0] is not LiteralExpr { Value: string filePath })
            throw new InvalidOperationException("Parse() first argument must be a string literal file path.");

        // Second arg: type hint as [TypeName]
        if (parseExpr.Args[1] is not ListLiteralExpr { Elements: { Count: 1 } elements }
            || elements[0] is not IdentifierExpr typeIdent)
        {
            throw new InvalidOperationException(
                "Parse() second argument must be a single-element type list, e.g. [Person].");
        }

        return (filePath, typeIdent.Name);
    }

    /// <summary>
    /// Extracts the type name from a Parse('file.json', [Type]) let declaration.
    /// </summary>
    private static string ExtractParseTypeName(LetDeclaration letDecl)
    {
        var (_, typeName) = ExtractParseArgs(letDecl);
        return typeName;
    }
}
