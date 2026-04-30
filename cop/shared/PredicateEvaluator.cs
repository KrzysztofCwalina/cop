using System.Collections;
using System.Text.RegularExpressions;

namespace Cop.Lang;

public class PredicateEvaluator
{

    private readonly Dictionary<string, List<PredicateDefinition>> _predicates;
    private readonly Dictionary<string, List<FunctionDefinition>> _functions;
    private readonly string _filePath;
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, LetDeclaration>? _letDeclarations;
    private readonly Dictionary<string, IList>? _resolvedCollections;
    private readonly IProviderQueryService? _providerQueryService;
    private readonly HashSet<string> _evaluatingLetValues = [];

    public PredicateEvaluator(
        Dictionary<string, List<PredicateDefinition>> predicates,
        string filePath,
        TypeRegistry registry,
        Dictionary<string, LetDeclaration>? letDeclarations = null,
        Dictionary<string, List<FunctionDefinition>>? functions = null,
        Dictionary<string, IList>? resolvedCollections = null,
        IProviderQueryService? providerQueryService = null)
    {
        _predicates = predicates;
        _filePath = filePath;
        _registry = registry;
        _letDeclarations = letDeclarations;
        _functions = functions ?? [];
        _resolvedCollections = resolvedCollections;
        _providerQueryService = providerQueryService;
    }

    public (bool result, EvaluationContext context) EvaluateAsBool(
        Expression expr, object item, string paramType)
    {
        var ctx = new EvaluationContext();
        bool result = ToBool(Eval(expr, item, paramType, ctx));
        return (result, ctx);
    }

    /// <summary>
    /// Evaluate an expression against an item, returning the raw value.
    /// Used by :select() to project collection items to field values.
    /// </summary>
    public object? EvaluateField(Expression expr, object item, string paramType)
    {
        var ctx = new EvaluationContext();
        return Eval(expr, item, paramType, ctx);
    }

    private object? Eval(Expression expr, object item, string paramType, EvaluationContext ctx)
    {
        return expr switch
        {
            NicExpr => null,
            LiteralExpr lit => lit.Value,
            ListLiteralExpr list => list.Elements.Select(e => Eval(e, item, paramType, ctx)).ToList(),
            ObjectLiteralExpr obj => EvalObjectLiteral(obj, item, paramType, ctx),
            IdentifierExpr id => EvalIdentifier(id.Name, item, paramType, ctx),
            MemberAccessExpr ma => GetMember(Eval(ma.Target, item, paramType, ctx), ma.Member),
            PredicateCallExpr mc => EvalPredicateCall(mc, item, paramType, ctx),
            FunctionCallExpr fc => EvalFunctionCall(fc, item, paramType, ctx),
            BinaryExpr bin => EvalBinary(bin, item, paramType, ctx),
            UnaryExpr { Operator: "!" } un => !ToBool(Eval(un.Operand, item, paramType, ctx)),
            ConditionalExpr cond => ToBool(Eval(cond.Condition, item, paramType, ctx))
                ? Eval(cond.TrueExpr, item, paramType, ctx)
                : Eval(cond.FalseExpr, item, paramType, ctx),
            MatchExpr match => EvalMatch(match, item, paramType, ctx),
            _ => throw new InvalidOperationException($"Unsupported expression: {expr}")
        };
    }

    private object? EvalObjectLiteral(ObjectLiteralExpr obj, object item, string paramType, EvaluationContext ctx)
    {
        var fields = new Dictionary<string, object?>();
        foreach (var (name, expr) in obj.Fields)
            fields[name] = Eval(expr, item, paramType, ctx);
        return new ScriptObject(obj.TypeName ?? "Object", fields);
    }

    private object? EvalFunctionCall(FunctionCallExpr fc, object item, string paramType, EvaluationContext ctx)
    {
        // Check user-defined functions first
        if (_functions.TryGetValue(fc.Name, out var funcGroup))
        {
            var func = ResolveFunction(funcGroup, paramType);
            return ApplyFunction(func, item, fc.Args, item, paramType, ctx);
        }

        // Fall back to built-in functions
        return CallFunction(fc.Name, fc.Args, item, paramType, ctx);
    }

    private object? EvalPredicateCall(PredicateCallExpr mc, object item, string paramType, EvaluationContext ctx)
    {
        // Check if this is a function call (transforms type, not a boolean filter)
        if (_functions.TryGetValue(mc.Name, out var funcGroup))
        {
            if (mc.Negated)
                throw new InvalidOperationException($"Cannot negate function call '{mc.Name}' — functions produce values, not booleans");
            var func = ResolveFunction(funcGroup, paramType);
            var target = Eval(mc.Target, item, paramType, ctx);
            return ApplyFunction(func, target, mc.Args, item, paramType, ctx);
        }

        // Path-scoped collection: namespace.Collection('path') → query provider
        if (_providerQueryService is not null
            && mc.Target is IdentifierExpr provId
            && mc.Args.Count == 1
            && mc.Args[0] is LiteralExpr { Value: string pathValue }
            && mc.Name.Length > 0 && char.IsUpper(mc.Name[0]))
        {
            return _providerQueryService.Query(provId.Name, mc.Name, pathValue);
        }

        var result = CallPredicate(Eval(mc.Target, item, paramType, ctx), mc.Name, mc.Args, item, paramType, ctx);
        return mc.Negated ? !ToBool(result) : result;
    }

    private object? EvalIdentifier(string name, object item, string paramType, EvaluationContext ctx)
    {
        if (name == paramType) return item;
        if (name == "item") return item;
        if (name == "null") return null;

        if (_predicates.TryGetValue(name, out var group))
        {
            var pred = ResolvePredicate(group, item, paramType, ctx);
            if (pred is null) return false; // no matching overload
            return ToBool(Eval(pred.Body, item, pred.ParameterType, ctx));
        }

        // Built-in 'empty' predicate — checks collections and strings on the item
        if (name == "empty")
        {
            if (item is string s) return s.Length == 0;
            if (item is IList col) return col.Count == 0;
            // For objects, check boolean 'Empty' property via registry
            var typeName = _registry.InferTypeName(item);
            if (typeName is not null)
            {
                var prop = _registry.GetType(typeName)?.GetProperty("Empty");
                if (prop?.Accessor is not null)
                    return ToBool(prop.Accessor(item));
            }
            return false;
        }

        // Let-bound value (e.g., let TestKeywords = ["Test", "Tests", ...])
        if (_letDeclarations is not null &&
            _letDeclarations.TryGetValue(name, out var letDecl) &&
            letDecl.IsValueBinding)
        {
            if (!_evaluatingLetValues.Add(name))
                throw new InvalidOperationException($"Circular let value reference: '{name}'");
            try
            {
                return Eval(letDecl.ValueExpression!, item, paramType, ctx);
            }
            finally
            {
                _evaluatingLetValues.Remove(name);
            }
        }

        // Let with SourceExpression fallback (decomposed as collection but actually a value expr)
        if (_letDeclarations is not null &&
            _letDeclarations.TryGetValue(name, out var letDeclExpr) &&
            !letDeclExpr.IsValueBinding &&
            letDeclExpr.SourceExpression is not null)
        {
            // Only treat as value if it's not resolved as a collection
            if (_resolvedCollections is null || !_resolvedCollections.ContainsKey(name))
            {
                if (!_evaluatingLetValues.Add(name))
                    throw new InvalidOperationException($"Circular let value reference: '{name}'");
                try
                {
                    return Eval(letDeclExpr.SourceExpression, item, paramType, ctx);
                }
                finally
                {
                    _evaluatingLetValues.Remove(name);
                }
            }
        }

        // Resolved collection binding (e.g., let factoryTypes = Code.Types:where(isFactory))
        if (_resolvedCollections is not null &&
            _resolvedCollections.TryGetValue(name, out var resolvedList))
        {
            return resolvedList;
        }

        // Check ancestor scope (e.g., Type accessible from Method predicates)
        var ancestor = ctx.GetAncestor(name);
        if (ancestor is not null) return ancestor;

        // Flags constant resolution (e.g., Public → 1, Static → 16)
        // Must come before language filter fallback, which would return false for
        // any identifier that doesn't match the file's language.
        var flagsValue = _registry.TryResolveFlagsConstant(name);
        if (flagsValue is not null) return flagsValue.Value;

        // Language filter fallback: if the item has a File.Language property,
        // check if the identifier matches the language. This enables filter chains
        // like Types:csharp:client where "csharp" matches File.Language == "csharp".
        var itemTypeName = _registry.InferTypeName(item);
        if (itemTypeName is not null)
        {
            var fileDesc = _registry.GetType(itemTypeName)?.GetProperty("File");
            if (fileDesc?.Accessor is not null)
            {
                var file = fileDesc.Accessor(item);
                if (file is not null)
                {
                    var fileTypeName = _registry.InferTypeName(file);
                    if (fileTypeName is not null)
                    {
                        var langDesc = _registry.GetType(fileTypeName)?.GetProperty("Language");
                        if (langDesc?.Accessor is not null)
                        {
                            var lang = langDesc.Accessor(file);
                            return lang is string langStr &&
                                   string.Equals(langStr, name, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException($"Unknown identifier '{name}'");
    }

    /// <summary>
    /// Resolves the best predicate overload: constrained match first, then unconstrained fallback.
    /// A constraint (e.g., predicate client(Type:csharp)) is evaluated as a predicate against the item.
    /// When paramType is "item" (inline lambda context), infers actual type for matching.
    /// </summary>
    private PredicateDefinition? ResolvePredicate(List<PredicateDefinition> group, object item, string paramType, EvaluationContext ctx)
    {
        // In inline lambda contexts, infer actual type for overload matching
        var matchType = paramType == "item"
            ? (_registry.InferTypeName(item) ?? "item")
            : paramType;

        PredicateDefinition? typeMatch = null;
        PredicateDefinition? unconstrained = null;
        foreach (var pred in group)
        {
            if (pred.Constraint is not null)
            {
                // Evaluate the constraint predicate against the item
                if (_predicates.TryGetValue(pred.Constraint, out var constraintGroup))
                {
                    // Find the unconstrained overload of the constraint predicate
                    var constraintPred = constraintGroup.FirstOrDefault(p => p.Constraint is null);
                    if (constraintPred is not null && ToBool(Eval(constraintPred.Body, item, constraintPred.ParameterType, ctx)))
                        return pred;
                }
            }
            else if (pred.ParameterType == matchType)
            {
                typeMatch = pred;
            }
            else
            {
                unconstrained = pred;
            }
        }
        return typeMatch ?? unconstrained;
    }

    private object? EvalBinary(BinaryExpr bin, object item, string paramType, EvaluationContext ctx)
    {
        return bin.Operator switch
        {
            "&&" => ToBool(Eval(bin.Left, item, paramType, ctx))
                     && ToBool(Eval(bin.Right, item, paramType, ctx)),
            "||" => ToBool(Eval(bin.Left, item, paramType, ctx))
                     || ToBool(Eval(bin.Right, item, paramType, ctx)),
            "&" => ToInt(Eval(bin.Left, item, paramType, ctx))
                     & ToInt(Eval(bin.Right, item, paramType, ctx)),
            "|" => ToInt(Eval(bin.Left, item, paramType, ctx))
                     | ToInt(Eval(bin.Right, item, paramType, ctx)),
            "==" => ValuesEqual(
                Eval(bin.Left, item, paramType, ctx),
                Eval(bin.Right, item, paramType, ctx)),
            "!=" => !ValuesEqual(
                Eval(bin.Left, item, paramType, ctx),
                Eval(bin.Right, item, paramType, ctx)),
            ">" or "<" or ">=" or "<=" => CompareValues(
                Eval(bin.Left, item, paramType, ctx),
                bin.Operator,
                Eval(bin.Right, item, paramType, ctx)),
            "+" => EvalAdd(
                Eval(bin.Left, item, paramType, ctx),
                Eval(bin.Right, item, paramType, ctx)),
            "-" => EvalSubtract(
                Eval(bin.Left, item, paramType, ctx),
                Eval(bin.Right, item, paramType, ctx)),
            _ => throw new InvalidOperationException($"Unknown operator '{bin.Operator}'")
        };
    }

    private object? EvalMatch(MatchExpr match, object item, string paramType, EvaluationContext ctx)
    {
        var discriminant = Eval(match.Discriminant, item, paramType, ctx);
        foreach (var arm in match.Arms)
        {
            if (arm.Pattern is null)
                return Eval(arm.Result, item, paramType, ctx); // wildcard _ matches everything

            var pattern = Eval(arm.Pattern, item, paramType, ctx);
            if (ValuesEqual(discriminant, pattern))
                return Eval(arm.Result, item, paramType, ctx);
        }
        return null; // no match, no default
    }

    private static bool CompareValues(object? a, string op, object? b)
    {
        double ad = ToDouble(a);
        double bd = ToDouble(b);
        return op switch
        {
            ">" => ad > bd,
            "<" => ad < bd,
            ">=" => ad >= bd,
            "<=" => ad <= bd,
            _ => false
        };
    }

    private static object EvalAdd(object? left, object? right)
    {
        // List + List → new concatenated list
        if (left is IList leftList && right is IList rightList)
        {
            var result = new List<object?>(leftList.Count + rightList.Count);
            foreach (var item in leftList) result.Add(item);
            foreach (var item in rightList) result.Add(item);
            return result;
        }
        // List + element → new list with element appended
        if (left is IList list)
        {
            var result = new List<object?>(list.Count + 1);
            foreach (var item in list) result.Add(item);
            result.Add(right);
            return result;
        }
        // String + String → concatenation
        if (left is string ls && right is string rs)
            return ls + rs;
        // Numeric addition
        if (left is int li && right is int ri)
            return li + ri;
        return ToDouble(left) + ToDouble(right);
    }

    private static object EvalSubtract(object? left, object? right)
    {
        if (left is int li && right is int ri)
            return li - ri;
        return ToDouble(left) - ToDouble(right);
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        double d => (int)d,
        bool b => b ? 1 : 0,
        string s when int.TryParse(s, out int n) => n,
        _ => 0
    };

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
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string str, object? listArg)
    {
        if (listArg is IList list)
        {
            foreach (var item in list)
            {
                var s = item?.ToString();
                if (!string.IsNullOrEmpty(s) && str.Contains(s, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Splits an identifier into lowercase words, handling camelCase, PascalCase,
    /// snake_case, kebab-case, and UPPER_CASE conventions.
    /// </summary>
    internal static List<object> SplitIdentifierWords(string identifier)
    {
        var words = new List<object>();
        int start = 0;
        for (int i = 0; i < identifier.Length; i++)
        {
            char c = identifier[i];
            if (c == '_' || c == '-')
            {
                if (i > start) words.Add(identifier[start..i].ToLowerInvariant());
                start = i + 1;
            }
            else if (i > start && char.IsUpper(c))
            {
                // Handle transitions: "taskCompletion" → split before C
                // Handle acronyms: "HTTPClient" → "HTTP" + "Client" (split before last upper of a run)
                bool prevIsUpper = char.IsUpper(identifier[i - 1]);
                bool nextIsLower = i + 1 < identifier.Length && char.IsLower(identifier[i + 1]);
                if (!prevIsUpper || (prevIsUpper && nextIsLower))
                {
                    words.Add(identifier[start..i].ToLowerInvariant());
                    start = i;
                }
            }
        }
        if (start < identifier.Length) words.Add(identifier[start..].ToLowerInvariant());
        return words;
    }

    /// <summary>
    /// Normalizes an identifier to a canonical form by splitting into words
    /// and joining lowercase (no separators). "Foo_Bar", "FooBar", "foo_bar" all → "foobar".
    /// </summary>
    internal static string NormalizeIdentifier(string identifier)
    {
        var words = SplitIdentifierWords(identifier);
        return string.Concat(words.Cast<string>());
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is int ai && b is int bi) return ai == bi;
        if (a is int or double && b is int or double) return ToDouble(a) == ToDouble(b);
        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private object? GetMember(object? target, string member)
    {
        if (target is null) return null;

        // ScriptObject: resolve fields by name, plus map properties
        if (target is ScriptObject ao)
        {
            return member switch
            {
                "Keys" => ao.Fields.Keys.ToList<object>(),
                "Values" => ao.Fields.Values.Where(v => v is not null).Cast<object>().ToList(),
                "Count" => ao.Fields.Count,
                _ => ao.GetField(member)
            };
        }

        // Collection properties (Count, First, Last, Single) — built-in, no registry
        if (target is IList list)
        {
            switch (member)
            {
                case "Count": return (object)list.Count;
                case "First": return list.Count > 0 ? list[0] : null;
                case "Last": return list.Count > 0 ? list[list.Count - 1] : null;
                case "Single": return list.Count == 1 ? list[0] : null;
                default:
                    // Flatten: list.Property → SelectMany across all items
                    var flattened = new List<object?>();
                    foreach (var item in list)
                    {
                        if (item is null) continue;
                        var memberValue = GetMember(item, member);
                        if (memberValue is IList subList)
                        {
                            foreach (var sub in subList) flattened.Add(sub);
                        }
                        else if (memberValue is not null)
                        {
                            flattened.Add(memberValue);
                        }
                    }
                    return flattened;
            }
        }

        // String properties — built-in, no registry
        if (target is string str)
        {
            return member switch
            {
                "Length" => (object)str.Length,
                "Lower" => str.ToLowerInvariant(),
                "Upper" => str.ToUpperInvariant(),
                "Normalized" => NormalizeIdentifier(str),
                "Words" => (object)SplitIdentifierWords(str),
                _ => null
            };
        }

        // Use type registry for all model types
        var typeName = _registry.InferTypeName(target);
        if (typeName is not null)
        {
            var typeDesc = _registry.GetType(typeName);
            if (typeDesc is not null)
            {
                // Map-like properties on any typed object
                if (member == "Keys")
                    return typeDesc.GetAllProperties().Select(p => (object)p.Name).ToList();
                if (member == "Values")
                    return typeDesc.GetAllProperties()
                        .Select(p => p.Accessor is not null ? p.Accessor(target) : null)
                        .Where(v => v is not null).Cast<object>().ToList();
                if (member == "Count")
                    return typeDesc.GetAllProperties().Count();

                var desc = typeDesc.GetProperty(member);
                if (desc?.Accessor is not null)
                    return desc.Accessor(target);
            }
        }

        return null;
    }

    private object? CallPredicate(object? target, string predicate, List<Expression> args,
        object item, string paramType, EvaluationContext ctx)
    {
        if (target is null) return null;

        // Map/ScriptObject operations
        if (target is ScriptObject so)
        {
            switch (predicate)
            {
                case "Get":
                    var key = args.Count > 0 ? Eval(args[0], item, paramType, ctx)?.ToString() : null;
                    return key is not null ? so.GetField(key) : null;
                case "containsKey":
                    var ck = args.Count > 0 ? Eval(args[0], item, paramType, ctx)?.ToString() : null;
                    return ck is not null && so.HasField(ck);
            }
        }

        // Universal object operations: Get/containsKey work on any typed object via registry
        if (predicate is "Get" or "containsKey")
        {
            var objTypeName = _registry.InferTypeName(target);
            if (objTypeName is not null)
            {
                var typeDesc = _registry.GetType(objTypeName);
                if (typeDesc is not null)
                {
                    var propName = args.Count > 0 ? Eval(args[0], item, paramType, ctx)?.ToString() : null;
                    if (propName is null) return predicate == "containsKey" ? false : null;
                    if (predicate == "containsKey")
                        return typeDesc.GetProperty(propName) is not null;
                    else // Get
                    {
                        var propDesc = typeDesc.GetProperty(propName);
                        return propDesc?.Accessor is not null ? propDesc.Accessor(target) : null;
                    }
                }
            }
        }

        // Universal predicates (work on any value type)
        if (predicate == "in" && args.Count > 0)
        {
            var evalList = Eval(args[0], item, paramType, ctx);
            if (evalList is IList list)
            {
                foreach (var listItem in list)
                {
                    if (ValuesEqual(target, listItem)) return true;
                }
            }
            return false;
        }

        // String predicates (also handle registered text-convertible types as string-like)
        string? str = target as string ?? _registry.ConvertToTextIfRegistered(target);
        if (str is not null)
        {
            var arg0 = args.Count > 0 ? Eval(args[0], item, paramType, ctx) : null;
            return predicate switch
            {
                "equals" => str.Equals(arg0?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
                "notEquals" => !str.Equals(arg0?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
                "endsWith" => str.EndsWith(arg0?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
                "startsWith" => str.StartsWith(arg0?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
                "contains" => str.Contains(arg0?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
                "containsAny" => ContainsAny(str, arg0),
                "matches" => Regex.IsMatch(str, arg0?.ToString() ?? "",
                    RegexOptions.None, TimeSpan.FromSeconds(1)),
                "Trim" => arg0 is not null && str.EndsWith(arg0.ToString()!, StringComparison.OrdinalIgnoreCase)
                    ? str[..^arg0.ToString()!.Length] : str,
                "Replace" => arg0 is not null
                    ? str.Replace(arg0.ToString()!, args.Count > 1
                        ? Eval(args[1], item, paramType, ctx)?.ToString() ?? "" : "", StringComparison.OrdinalIgnoreCase)
                    : str,
                "sameAs" => NormalizeIdentifier(str) == NormalizeIdentifier(arg0?.ToString() ?? ""),
                "empty" => (object)(str.Length == 0),
                _ => throw new InvalidOperationException($"Unknown string predicate '{predicate}'")
            };
        }

        // Numeric predicates (int, long, double, float)
        if (target is int or long or double or float)
        {
            double num = ToDouble(target);
            var arg0 = args.Count > 0 ? ToDouble(Eval(args[0], item, paramType, ctx)) : 0;
            return predicate switch
            {
                "equals" => num == arg0,
                "notEquals" => num != arg0,
                "greaterThan" => num > arg0,
                "lessThan" => num < arg0,
                "greaterOrEqual" => num >= arg0,
                "lessOrEqual" => num <= arg0,
                "isSet" => ((long)num & (long)arg0) != 0,
                "isClear" => ((long)num & (long)arg0) == 0,
                _ => throw new InvalidOperationException($"Unknown numeric predicate '{predicate}'")
            };
        }

        // Collection predicates (must check before method evaluators since
        // collection predicate args are evaluated per-item, not eagerly)
        if (target is IList collection)
            return CallCollectionPredicate(collection, predicate, args, item, paramType, ctx);

        // Registered method evaluators (e.g., Type.inheritsFrom)
        var evalArgs = args.Select(a => Eval(a, item, paramType, ctx)).ToList();
        var methodResult = _registry.TryEvaluateMethod(target, predicate, evalArgs);
        if (methodResult is not null)
            return methodResult;

        // User-defined predicates called on an object (e.g., Type:isPublic)
        if (_predicates.TryGetValue(predicate, out var predGroup))
        {
            var pred = ResolvePredicate(predGroup, target, paramType, ctx);
            if (pred is null) return false;
            return ToBool(Eval(pred.Body, target, pred.ParameterType, ctx));
        }

        throw new InvalidOperationException($"Cannot call predicate '{predicate}' on {target.GetType().Name}");
    }

    private object? CallCollectionPredicate(IList collection, string predicate, List<Expression> args,
        object item, string paramType, EvaluationContext ctx)
    {
        // Push current item as ancestor so nested predicates can reference enclosing scope
        ctx.PushAncestor(paramType, item);

        switch (predicate)
        {
            case "any":
            {
                var predExpr = args[0];
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(predExpr, collItem);
                    if (ToBool(Eval(predExpr, collItem, itemType, ctx)))
                    {
                        ctx.Capture(itemType, collItem);
                        return true;
                    }
                }
                return false;
            }
            case "none":
            {
                var predExpr = args[0];
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(predExpr, collItem);
                    if (ToBool(Eval(predExpr, collItem, itemType, ctx)))
                        return false;
                }
                return true;
            }
            case "all":
            {
                var predExpr = args[0];
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(predExpr, collItem);
                    if (!ToBool(Eval(predExpr, collItem, itemType, ctx)))
                        return false;
                }
                return true;
            }
            case "Where":
            {
                var predExpr = args[0];
                var result = new List<object>();
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(predExpr, collItem);
                    if (ToBool(Eval(predExpr, collItem, itemType, ctx)))
                        result.Add(collItem);
                }
                return result;
            }
            case "contains":
            {
                var value = Eval(args[0], item, paramType, ctx)?.ToString();
                foreach (var collItem in collection)
                {
                    if (string.Equals(collItem?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            case "empty":
            {
                return collection.Count == 0;
            }
            case "First":
            {
                if (args.Count > 0)
                {
                    var predExpr = args[0];
                    foreach (var collItem in collection)
                    {
                        if (collItem is null) continue;
                        string itemType = InferItemType(predExpr, collItem);
                        if (ToBool(Eval(predExpr, collItem, itemType, ctx)))
                        {
                            ctx.Capture(itemType, collItem);
                            return collItem;
                        }
                    }
                    return null;
                }
                return collection.Count > 0 ? collection[0] : null;
            }
            case "Last":
            {
                if (args.Count > 0)
                {
                    var predExpr = args[0];
                    for (int i = collection.Count - 1; i >= 0; i--)
                    {
                        var collItem = collection[i];
                        if (collItem is null) continue;
                        string itemType = InferItemType(predExpr, collItem);
                        if (ToBool(Eval(predExpr, collItem, itemType, ctx)))
                        {
                            ctx.Capture(itemType, collItem);
                            return collItem;
                        }
                    }
                    return null;
                }
                return collection.Count > 0 ? collection[collection.Count - 1] : null;
            }
            case "Single":
            {
                if (args.Count > 0)
                {
                    var predExpr = args[0];
                    object? match = null;
                    int matchCount = 0;
                    foreach (var collItem in collection)
                    {
                        if (collItem is null) continue;
                        string itemType = InferItemType(predExpr, collItem);
                        if (ToBool(Eval(predExpr, collItem, itemType, ctx)))
                        {
                            match = collItem;
                            matchCount++;
                            if (matchCount > 1) return null;
                        }
                    }
                    if (matchCount == 1 && match is not null)
                    {
                        string itemType = InferItemType(predExpr, match);
                        ctx.Capture(itemType, match);
                        return match;
                    }
                    return null;
                }
                return collection.Count == 1 ? collection[0] : null;
            }
            case "ElementAt":
            {
                var index = ToInt(Eval(args[0], item, paramType, ctx));
                return index >= 0 && index < collection.Count ? collection[index] : null;
            }
            case "Select":
            {
                // Project each item via a field/expression. Preserves value types.
                var fieldExpr = args[0];
                var result = new List<object>();
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(fieldExpr, collItem);
                    var value = Eval(fieldExpr, collItem, itemType, ctx);
                    if (value is not null)
                        result.Add(value);
                }
                return result;
            }
            case "OrderBy":
            {
                var fieldExpr = args[0];
                var sorted = collection.Cast<object>().Where(x => x is not null).ToList();
                sorted.Sort((a, b) =>
                {
                    string aType = InferItemType(fieldExpr, a);
                    string bType = InferItemType(fieldExpr, b);
                    var aVal = Eval(fieldExpr, a, aType, ctx);
                    var bVal = Eval(fieldExpr, b, bType, ctx);
                    return CompareForSort(aVal, bVal);
                });
                return sorted;
            }
            case "OrderByDescending":
            {
                var fieldExpr = args[0];
                var sorted = collection.Cast<object>().Where(x => x is not null).ToList();
                sorted.Sort((a, b) =>
                {
                    string aType = InferItemType(fieldExpr, a);
                    string bType = InferItemType(fieldExpr, b);
                    var aVal = Eval(fieldExpr, a, aType, ctx);
                    var bVal = Eval(fieldExpr, b, bType, ctx);
                    return CompareForSort(bVal, aVal); // reversed
                });
                return sorted;
            }
            case "Sum":
            {
                var fieldExpr = args[0];
                double sum = 0;
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(fieldExpr, collItem);
                    sum += ToDouble(Eval(fieldExpr, collItem, itemType, ctx));
                }
                return (int)sum == sum ? (object)(int)sum : sum;
            }
            case "Min":
            {
                var fieldExpr = args[0];
                double? min = null;
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(fieldExpr, collItem);
                    var val = ToDouble(Eval(fieldExpr, collItem, itemType, ctx));
                    if (min is null || val < min) min = val;
                }
                return min is null ? 0 : ((int)min.Value == min.Value ? (object)(int)min.Value : min.Value);
            }
            case "Max":
            {
                var fieldExpr = args[0];
                double? max = null;
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(fieldExpr, collItem);
                    var val = ToDouble(Eval(fieldExpr, collItem, itemType, ctx));
                    if (max is null || val > max) max = val;
                }
                return max is null ? 0 : ((int)max.Value == max.Value ? (object)(int)max.Value : max.Value);
            }
            case "Average":
            {
                var fieldExpr = args[0];
                double sum = 0;
                int count = 0;
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(fieldExpr, collItem);
                    sum += ToDouble(Eval(fieldExpr, collItem, itemType, ctx));
                    count++;
                }
                return count > 0 ? sum / count : 0.0;
            }
            case "Distinct":
            {
                if (args.Count > 0)
                {
                    // Distinct by expression: deduplicate by projected value
                    var fieldExpr = args[0];
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var result = new List<object>();
                    foreach (var collItem in collection)
                    {
                        if (collItem is null) continue;
                        string itemType = InferItemType(fieldExpr, collItem);
                        var key = Eval(fieldExpr, collItem, itemType, ctx)?.ToString() ?? "";
                        if (seen.Add(key))
                            result.Add(collItem);
                    }
                    return result;
                }
                else
                {
                    // Distinct without args: deduplicate by string representation
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var result = new List<object>();
                    foreach (var collItem in collection)
                    {
                        if (collItem is null) continue;
                        var key = collItem.ToString() ?? "";
                        if (seen.Add(key))
                            result.Add(collItem);
                    }
                    return result;
                }
            }
            case "GroupBy":
            {
                var fieldExpr = args[0];
                var groups = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
                var groupOrder = new List<string>();
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(fieldExpr, collItem);
                    var key = Eval(fieldExpr, collItem, itemType, ctx)?.ToString() ?? "";
                    if (!groups.TryGetValue(key, out var groupList))
                    {
                        groupList = new List<object>();
                        groups[key] = groupList;
                        groupOrder.Add(key);
                    }
                    groupList.Add(collItem);
                }
                // Return as list of ScriptObjects with Key and Items properties
                var result = new List<object>();
                foreach (var key in groupOrder)
                {
                    var groupObj = new ScriptObject("Group");
                    groupObj.Set("Key", key);
                    groupObj.Set("Items", groups[key]);
                    groupObj.Set("Count", groups[key].Count);
                    result.Add(groupObj);
                }
                return result;
            }
            case "Reduce":
            {
                // Reduce(operator, itemExpr, separator?, seed?)
                // operator is passed as a string literal ('+')
                // For now, support string concatenation with separator
                if (args.Count < 2)
                    throw new InvalidOperationException("Reduce requires at least operator and item expression");

                var opExpr = args[0];
                var fieldExpr = args[1];
                var separator = args.Count > 2 ? Eval(args[2], item, paramType, ctx)?.ToString() ?? "" : "";
                var seed = args.Count > 3 ? Eval(args[3], item, paramType, ctx) : null;

                var op = opExpr is LiteralExpr lit ? lit.Value?.ToString() : 
                         opExpr is IdentifierExpr id2 ? id2.Name : "+";

                var values = new List<object?>();
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(fieldExpr, collItem);
                    values.Add(Eval(fieldExpr, collItem, itemType, ctx));
                }

                if (op == "+")
                {
                    // Check if numeric or string based on first value or seed
                    bool isNumeric = seed is int or double || (seed is null && values.Count > 0 && values[0] is int or double);
                    if (isNumeric)
                    {
                        double result = ToDouble(seed);
                        foreach (var val in values)
                            result += ToDouble(val);
                        return (int)result == result ? (object)(int)result : result;
                    }
                    else
                    {
                        // String concatenation with separator
                        var seedStr = seed?.ToString() ?? "";
                        var parts = values.Where(v => v is not null).Select(v => v!.ToString()!).ToList();
                        return seedStr + string.Join(separator, parts);
                    }
                }

                throw new InvalidOperationException($"Unsupported Reduce operator: '{op}'");
            }
            default:
                throw new InvalidOperationException($"Unknown collection predicate '{predicate}'");
        }
    }

    /// <summary>
    /// In inline lambda contexts (Where, Select, any, etc.), only "item" is the
    /// implicit variable. Named predicates still resolve via their declared ParameterType.
    /// </summary>
    private string InferItemType(Expression predExpr, object collItem)
    {
        // Named predicate reference — use its declared parameter type for constraint resolution
        if (predExpr is IdentifierExpr id && _predicates.TryGetValue(id.Name, out var group))
        {
            var pred = group.FirstOrDefault();
            if (pred is not null) return pred.ParameterType;
        }

        // For inline expressions (item.Name, item:isPublic, etc.), use "item" as the
        // lambda variable. The actual type is inferred dynamically in ResolvePredicate.
        return "item";
    }

    private object? CallFunction(string name, List<Expression> args,
        object item, string paramType, EvaluationContext ctx)
    {
        switch (name)
        {
            case "Text":
            {
                var value = Eval(args[0], item, paramType, ctx);
                return ConvertToText(value);
            }
            case "File":
            {
                var path = Eval(args[0], item, paramType, ctx)?.ToString() ?? "";
                return ReadFileSandboxed(path);
            }
            case "Path":
            {
                var pattern = Eval(args[0], item, paramType, ctx)?.ToString() ?? "";
                return GlobMatch(_filePath, pattern);
            }
            case "Matches":
            {
                string text = item switch
                {
                    string s => s,
                    _ => _registry.ConvertToText(item)
                };
                var pattern = Eval(args[0], item, paramType, ctx)?.ToString() ?? "";
                return Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            default:
                throw new InvalidOperationException($"Unknown function '{name}'");
        }
    }

    public static bool GlobMatch(string path, string pattern)
    {
        var p = path.Replace('\\', '/');
        var g = pattern.Replace('\\', '/');
        return GlobMatchRecursive(p, 0, g, 0);
    }

    private static bool GlobMatchRecursive(string path, int pi, string glob, int gi)
    {
        while (gi < glob.Length && pi < path.Length)
        {
            if (gi + 1 < glob.Length && glob[gi] == '*' && glob[gi + 1] == '*')
            {
                gi += 2;
                if (gi < glob.Length && glob[gi] == '/') gi++;
                // Try matching 0..N characters
                for (int i = pi; i <= path.Length; i++)
                {
                    if (GlobMatchRecursive(path, i, glob, gi))
                        return true;
                }
                return false;
            }

            if (glob[gi] == '*')
            {
                gi++;
                for (int i = pi; i <= path.Length; i++)
                {
                    if (i > pi && path[i - 1] == '/') break;
                    if (GlobMatchRecursive(path, i, glob, gi))
                        return true;
                }
                return false;
            }

            if (glob[gi] == '?')
            {
                if (path[pi] == '/') return false;
                gi++;
                pi++;
            }
            else if (glob[gi] == path[pi])
            {
                gi++;
                pi++;
            }
            else
            {
                return false;
            }
        }

        // Handle trailing wildcards
        while (gi + 1 < glob.Length && glob[gi] == '*' && glob[gi + 1] == '*') gi += 2;
        while (gi < glob.Length && glob[gi] == '*') gi++;
        while (gi < glob.Length && glob[gi] == '/') gi++;

        return gi >= glob.Length && pi >= path.Length;
    }

    private static bool ToBool(object? value) => value switch
    {
        bool b => b,
        null => false,
        _ => true
    };

    private static string ConvertToText(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        int i => i.ToString(),
        byte by => by.ToString(),
        IList list => $"[{string.Join(", ", list.Cast<object>().Select(ConvertToText))}]",
        _ => value.ToString() ?? ""
    };

    private const int MaxFileSize = 10 * 1024 * 1024; // 10 MB

    private byte[] ReadFileSandboxed(string path)
    {
        // Resolve relative to the source file being processed
        var dir = Path.GetDirectoryName(Path.GetFullPath(_filePath)) ?? ".";
        var fullPath = Path.GetFullPath(Path.Combine(dir, path));

        if (!System.IO.File.Exists(fullPath))
            throw new InvalidOperationException($"File not found: {path}");

        var info = new FileInfo(fullPath);
        if (info.Length > MaxFileSize)
            throw new InvalidOperationException($"File too large (max {MaxFileSize / 1024 / 1024}MB): {path}");

        return System.IO.File.ReadAllBytes(fullPath);
    }

    /// <summary>
    /// Apply a function definition to an item, producing an ScriptObject.
    /// Evaluates each field mapping expression with the item as context,
    /// binding function parameters from the provided arguments.
    /// </summary>
    private object? ApplyFunction(FunctionDefinition func, object? target, List<Expression> callArgs,
        object item, string paramType, EvaluationContext ctx)
    {
        // The target is the item being transformed
        var inputItem = target ?? item;
        var inputType = func.InputType;

        // Bind function parameters from call arguments
        // e.g., function error(Statement, message: string) called as :error("Do not use var")
        // → binds "message" to "Do not use var"
        // String arguments with {item.Prop} templates are resolved against the input item.
        var paramBindings = new Dictionary<string, object?>();
        for (int i = 0; i < func.Parameters.Count && i < callArgs.Count; i++)
        {
            var argValue = Eval(callArgs[i], item, paramType, ctx);
            // Resolve string templates like {item.MemberName} in string arguments
            if (argValue is string strVal && strVal.Contains('{'))
                argValue = ResolveStringTemplate(strVal, inputItem, inputType);
            paramBindings[func.Parameters[i].Name] = argValue;
        }

        // Expression-body function: evaluate the body expression and return directly
        if (func.BodyExpression is not null)
        {
            var funcCtx = ctx.Clone();
            foreach (var (pName, pValue) in paramBindings)
                funcCtx.PushAncestor(pName, pValue!);
            if (inputItem is not null)
            {
                funcCtx.PushAncestor(inputType, inputItem);
                funcCtx.PushAncestor("item", inputItem);
            }
            return EvalInFunctionContext(func.BodyExpression, inputItem!, inputType, funcCtx, paramBindings);
        }

        // Record-body function: evaluate field mappings and return ScriptObject
        var fields = new Dictionary<string, object?>();
        foreach (var (fieldName, fieldExpr) in func.FieldMappings)
        {
            // Create a context where function parameters are accessible as identifiers
            var funcCtx = new EvaluationContext();
            // Copy parameter bindings into context
            foreach (var (pName, pValue) in paramBindings)
                funcCtx.Capture(pName, pValue);
            // Capture the input item as its type name and as "item"
            funcCtx.Capture(inputType, inputItem);
            funcCtx.Capture("item", inputItem);

            fields[fieldName] = EvalInFunctionContext(fieldExpr, inputItem, inputType, funcCtx, paramBindings);
        }

        return new ScriptObject(func.ReturnType, fields);
    }

    /// <summary>
    /// Evaluate an expression in function body context, where function parameters
    /// are available as plain identifiers (e.g., "message" resolves to the parameter value).
    /// </summary>
    private object? EvalInFunctionContext(Expression expr, object item, string paramType,
        EvaluationContext ctx, Dictionary<string, object?> paramBindings)
    {
        // For identifiers, check function parameters first
        if (expr is IdentifierExpr id && paramBindings.ContainsKey(id.Name))
            return paramBindings[id.Name];

        // For member access on the input type, resolve normally
        return Eval(expr, item, paramType, ctx);
    }

    /// <summary>
    /// Check if a name refers to a function definition.
    /// </summary>
    public bool IsFunction(string name) => _functions.ContainsKey(name);

    /// <summary>
    /// Resolve {item.Prop} patterns in a string, using the current item as context.
    /// Used for function string arguments like "Do not use var for {item.MemberName}".
    /// </summary>
    private string ResolveStringTemplate(string template, object item, string itemType)
    {
        var segments = TemplateParser.Parse(template);
        var sb = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            if (segment is LiteralSegment lit)
            {
                sb.Append(lit.Text);
            }
            else if (segment is AnnotatedLiteralSegment annLit)
            {
                sb.Append(annLit.Text);
            }
            else if (segment is ExpressionSegment expr)
            {
                object? obj = (expr.PropertyPath[0] == itemType || expr.PropertyPath[0] == "item") ? item : null;
                if (obj == null)
                {
                    // Preserve unresolved placeholder
                    sb.Append('{').Append(string.Join('.', expr.PropertyPath)).Append('}');
                    continue;
                }

                for (int i = 1; i < expr.PropertyPath.Length; i++)
                {
                    obj = GetMember(obj, expr.PropertyPath[i]);
                    if (obj == null) break;
                }
                sb.Append(obj?.ToString() ?? "");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Get the return type of a function by name, optionally matching input type for overloads.
    /// </summary>
    public string? GetFunctionReturnType(string name, string? inputType = null)
    {
        if (!_functions.TryGetValue(name, out var group)) return null;
        var func = ResolveFunction(group, inputType);
        return func.ReturnType;
    }

    /// <summary>
    /// Apply a function to an item, producing an ScriptObject.
    /// Used by the interpreter for map operations in collection chains.
    /// </summary>
    public object? ApplyFunction(string funcName, object item, string itemType, List<Expression> args)
    {
        if (!_functions.TryGetValue(funcName, out var group))
            throw new InvalidOperationException($"Unknown function '{funcName}'");
        var func = ResolveFunction(group, itemType);
        var ctx = new EvaluationContext();
        return ApplyFunction(func, item, args, item, itemType, ctx);
    }

    /// <summary>
    /// Resolve a function overload by matching input type.
    /// Prefers exact match, falls back to first definition.
    /// </summary>
    private static FunctionDefinition ResolveFunction(List<FunctionDefinition> group, string? inputType)
    {
        if (inputType != null)
        {
            var match = group.FirstOrDefault(f => f.InputType == inputType);
            if (match != null) return match;
        }
        return group[0];
    }
}
