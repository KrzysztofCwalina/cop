using System.Diagnostics;

namespace Cop.Lang;

public class ScriptInterpreter
{
    private readonly TypeRegistry _typeRegistry;
    private readonly int _maxOutputsPerCommand;
    private readonly TimeSpan _timeout;

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
        HashSet<string>? commandFilter = null)
    {
        var allOutputs = new List<PrintOutput>();
        var fileOutputs = new Dictionary<string, List<string>>();

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
            if (commandName != null)
            {
                // Run only matching named commands (legacy single-command mode)
                commandsToRun = ScriptFile.Commands.Where(c => c.IsCommand && string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));
            }
            else if (commandFilter != null)
            {
                // Run only commands whose name matches the filter (supports auto-derived names)
                commandsToRun = ScriptFile.Commands.Where(c =>
                    !IsSaveAction(c.ActionName) &&
                    commandFilter.Contains(c.Name));
            }
            else
            {
                // Run all commands but skip SAVE actions (side-effecting, require explicit invocation)
                commandsToRun = ScriptFile.Commands.Where(c => !IsSaveAction(c.ActionName));
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
                    ExecuteCommand(target, documents, predicateGroups, tempLets, functionGroups, program, allCommands, allOutputs, fileOutputs);
                    continue;
                }

                ExecuteCommand(cmd, documents, predicateGroups, letDeclarations, functionGroups, program, allCommands, allOutputs, fileOutputs);
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

                    ExecuteCommand(cmdTemplate, documents, predicateGroups, tempLets, functionGroups, program, allCommands, allOutputs, fileOutputs);
                }
                else
                {
                    ExecuteCommand(cmdTemplate, documents, predicateGroups, letDeclarations, functionGroups, program, allCommands, allOutputs, fileOutputs);
                }
            }
        }

        outputs = fileOutputs.Select(kv =>
            new FileOutput(kv.Key, string.Join(Environment.NewLine, kv.Value)))
            .ToList();

        return new InterpreterResult(allOutputs, outputs);
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
        Dictionary<string, List<string>> fileOutputs)
    {
        // Evaluate guard predicate if present
        if (cmd.Guard is not null)
        {
            if (!EvaluateGuard(cmd.Guard, program, predicateGroups, letDeclarations, functionGroups))
                return;
        }

        // Aggregate counts for template resolution
        var aggregateCounts = new Dictionary<string, int>();
        foreach (var otherCmd in allCommands.Values.SelectMany(c => c))
        {
            if (otherCmd.Collection is not null && otherCmd.Name is not null)
            {
                // We could compute counts here, but for now just use the command name
            }
        }

        // Bare command — no collection, execute once
        if (cmd.Collection is null)
        {
            var richMessage = ResolveAggregateTemplate(cmd.MessageTemplate, aggregateCounts);

            if (IsSaveAction(cmd.ActionName) && cmd.OutputPath is not null)
            {
                if (!fileOutputs.TryGetValue(cmd.OutputPath, out var lines))
                {
                    lines = [];
                    fileOutputs[cmd.OutputPath] = lines;
                }
                lines.Add(richMessage.ToPlainText());
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
                if (IsSaveAction(cmd.ActionName) && cmd.OutputPath is not null)
                {
                    if (!fileOutputs.TryGetValue(cmd.OutputPath, out var lines))
                    {
                        lines = [];
                        fileOutputs[cmd.OutputPath] = lines;
                    }
                    lines.Add(richMessage.ToPlainText());
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

            var evaluator = new PredicateEvaluator(predicateGroups, document.Path, _typeRegistry, letDeclarations, functionGroups);
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
                if (IsSaveAction(cmd.ActionName) && cmd.OutputPath is not null)
                {
                    if (!fileOutputs.TryGetValue(cmd.OutputPath, out var lines))
                    {
                        lines = [];
                        fileOutputs[cmd.OutputPath] = lines;
                    }
                    lines.Add(richMessage.ToPlainText());
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
        HashSet<string>? visited = null)
    {
        // Resolve dotted collection names (e.g., "Code.Statements" → "Statements")
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
                foreach (var elem in ((ListLiteralExpr)letDecl.ValueExpression!).Elements)
                {
                    var name = ((IdentifierExpr)elem).Name;
                    unionItems.AddRange(ResolveCollection(name, document, evaluator, predicateGroups, letDeclarations, functionGroups, new(visited)));
                }
                return unionItems;
            }

            // Value bindings (let Name = [...]) are not collections — skip
            if (letDecl.IsValueBinding)
                throw new InvalidOperationException($"'{collection}' is a value binding, not a collection");

            var baseItems = ResolveCollection(
                letDecl.BaseCollection, document, evaluator, predicateGroups, letDeclarations, functionGroups, visited);
            var baseItemType = ResolveItemType(letDecl.BaseCollection, predicateGroups, letDeclarations, functionGroups);

            // Apply inline filters (may include function map steps)
            var result = ApplyFilters(baseItems, baseItemType, letDecl.Filters, evaluator, functionGroups);

            // Apply set subtraction if exclusions are specified
            if (letDecl.Exclusions != null)
            {
                var finalType = ResolveItemTypeAfterFilters(baseItemType, letDecl.Filters, functionGroups);
                result = ApplyExclusions(result, finalType, letDecl.Exclusions, evaluator, letDeclarations);
            }

            return result;
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
    /// Apply a chain of filters to a list of items. Each filter is either:
    /// - A predicate (Where): keeps items matching the predicate
    /// - A function (Select/Map): transforms each item into a new typed object
    /// </summary>
    private List<object> ApplyFilters(
        List<object> items, string itemType, List<Expression> filters,
        PredicateEvaluator evaluator,
        Dictionary<string, List<FunctionDefinition>> functionGroups)
    {
        var currentItems = items;
        var currentType = itemType;

        foreach (var filter in filters)
        {
            // Detect if this filter is a function call
            var funcName = GetFunctionNameFromFilter(filter);
            if (funcName != null && functionGroups.ContainsKey(funcName))
            {
                // Map step: transform each item using the function
                var funcArgs = GetFilterArgs(filter);
                currentItems = currentItems.Select(item =>
                    (object)evaluator.ApplyFunction(funcName, item, currentType, funcArgs)).ToList();
                currentType = evaluator.GetFunctionReturnType(funcName) ?? currentType;
            }
            else
            {
                // Filter step: keep items where expression evaluates to true
                currentItems = currentItems.Where(item =>
                {
                    var (result, _) = evaluator.EvaluateAsBool(filter, item, currentType);
                    return result;
                }).ToList();
            }
        }

        return currentItems;
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
    /// Resolves a dotted collection name (e.g., "Code.Statements") to its property collection name.
    /// Validates that the parent object exists as a let binding and resolves the property
    /// to the corresponding collection name. Non-dotted names pass through unchanged.
    /// </summary>
    private static string ResolveDottedCollection(string collection, Dictionary<string, LetDeclaration> letDeclarations)
    {
        var dotIndex = collection.IndexOf('.');
        if (dotIndex < 0) return collection;

        var parentName = collection[..dotIndex];
        var propertyName = collection[(dotIndex + 1)..];

        // Verify the parent exists as a let declaration (typically a runtime:: binding like Code or Disk)
        if (!letDeclarations.ContainsKey(parentName))
        {
            // Parent may come from a transitive import not yet in scope — try property name directly
            return propertyName;
        }

        // The property name IS the collection name (Code.Statements → Statements, Disk.Folders → Folders)
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
                return ((ListLiteralExpr)letDecl.ValueExpression!).Elements.All(e =>
                    e is IdentifierExpr id && IsGlobalRootCollection(id.Name, predicateGroups, letDeclarations, new(visited)));
            }
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
            // Collection union: let Name = [a, b, c]
            if (letDecl.IsCollectionUnion)
            {
                var unionItems = new List<object>();
                foreach (var elem in ((ListLiteralExpr)letDecl.ValueExpression!).Elements)
                {
                    var name = ((IdentifierExpr)elem).Name;
                    unionItems.AddRange(ResolveGlobalCollection(name, evaluator, predicateGroups, letDeclarations, functionGroups, new(visited)));
                }
                return unionItems;
            }

            if (letDecl.IsValueBinding)
                throw new InvalidOperationException($"'{collection}' is a value binding, not a collection");

            var baseItems = ResolveGlobalCollection(
                letDecl.BaseCollection, evaluator, predicateGroups, letDeclarations, functionGroups, visited);
            var baseItemType = ResolveItemType(letDecl.BaseCollection, predicateGroups, letDeclarations, functionGroups);

            var result = ApplyFilters(baseItems, baseItemType, letDecl.Filters, evaluator, functionGroups);

            if (letDecl.Exclusions != null)
            {
                var finalType = ResolveItemTypeAfterFilters(baseItemType, letDecl.Filters, functionGroups);
                result = ApplyExclusions(result, finalType, letDecl.Exclusions, evaluator, letDeclarations);
            }

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
        // Resolve dotted collection names (e.g., "Code.Statements" → "Statements")
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
                var firstElem = ((ListLiteralExpr)letDecl.ValueExpression!).Elements[0];
                return ResolveItemType(((IdentifierExpr)firstElem).Name, predicateGroups, letDeclarations, functionGroups, new(visited));
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
            if (funcName != null && functionGroups != null && functionGroups.TryGetValue(funcName, out var group))
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

        // Include global collections (Folders, DiskFiles, etc.)
        foreach (var collName in new[] { "Folders", "DiskFiles" })
        {
            var items = _typeRegistry.GetGlobalCollectionItems(collName);
            if (items is not null)
                counts[collName] = items.Count;
        }

        return counts;
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
            else if (segment is ExpressionSegment expr && expr.PropertyPath.Length == 2)
            {
                var collName = expr.PropertyPath[0];
                var prop = expr.PropertyPath[1];
                if (prop == "Count" && aggregateCounts.TryGetValue(collName, out var count))
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
}
