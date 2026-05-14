using System.Collections;
using System.Text.RegularExpressions;

namespace Cop.Lang;

/// <summary>
/// Sentinel value representing a provider namespace in expressions (e.g., "http" in http.Get(...)).
/// When the evaluator resolves an identifier that is a provider function namespace, it returns
/// this sentinel. CallPredicate then dispatches method calls on it to provider functions.
/// </summary>
internal sealed class ProviderNamespaceRef(string name)
{
    public string Name { get; } = name;
    public override string ToString() => $"[namespace:{Name}]";
}

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

    // Package-qualified stores for disambiguation (packageName → symbolName → definitions)
    private readonly Dictionary<string, Dictionary<string, List<PredicateDefinition>>>? _packagePredicates;
    private readonly Dictionary<string, Dictionary<string, List<FunctionDefinition>>>? _packageFunctions;
    private readonly Dictionary<string, Dictionary<string, LetDeclaration>>? _packageLets;

    public PredicateEvaluator(
        Dictionary<string, List<PredicateDefinition>> predicates,
        string filePath,
        TypeRegistry registry,
        Dictionary<string, LetDeclaration>? letDeclarations = null,
        Dictionary<string, List<FunctionDefinition>>? functions = null,
        Dictionary<string, IList>? resolvedCollections = null,
        IProviderQueryService? providerQueryService = null,
        Dictionary<string, Dictionary<string, List<PredicateDefinition>>>? packagePredicates = null,
        Dictionary<string, Dictionary<string, List<FunctionDefinition>>>? packageFunctions = null,
        Dictionary<string, Dictionary<string, LetDeclaration>>? packageLets = null)
    {
        _predicates = predicates;
        _filePath = filePath;
        _registry = registry;
        _letDeclarations = letDeclarations;
        _functions = functions ?? [];
        _resolvedCollections = resolvedCollections;
        _providerQueryService = providerQueryService;
        _packagePredicates = packagePredicates;
        _packageFunctions = packageFunctions;
        _packageLets = packageLets;
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
            CollectionUnionExpr union => EvalCollectionUnion(union, item, paramType, ctx),
            ObjectLiteralExpr obj => EvalObjectLiteral(obj, item, paramType, ctx),
            IdentifierExpr id => EvalIdentifier(id.Name, item, paramType, ctx),
            MemberAccessExpr ma => EvalMemberAccess(ma, item, paramType, ctx),
            CallExpr { Target: not null } mc => EvalTargetedCall(mc, item, paramType, ctx),
            CallExpr fc => EvalStandaloneCall(fc, item, paramType, ctx),
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
        return new DataObject(obj.TypeName ?? "Object", fields);
    }

    private object? EvalCollectionUnion(CollectionUnionExpr union, object item, string paramType, EvaluationContext ctx)
    {
        var result = new List<object?>();
        foreach (var elem in union.Elements)
        {
            var val = Eval(elem, item, paramType, ctx);
            if (val is IList list)
                foreach (var listItem in list)
                    result.Add(listItem);
            else if (val is not null)
                result.Add(val);
        }
        return result;
    }

    private object? EvalStandaloneCall(CallExpr fc, object item, string paramType, EvaluationContext ctx)
    {
        // Built-in FAIL('message') — terminates execution immediately
        if (fc.Name == "FAIL")
        {
            string? message = null;
            if (fc.Args.Count > 0)
            {
                var msgValue = Eval(fc.Args[0], item, paramType, ctx);
                message = msgValue is string s ? s : msgValue?.ToString();
            }
            throw new FailException(message ?? "FAIL", _filePath, 0);
        }

        // Built-in data(providerName) — returns a dynamic DataObject for any provider
        if (fc.Name == "data")
            return EvalDataFunction(fc.Args, item, paramType, ctx);

        // Built-in source(providerName) — returns a streaming source handle
        if (fc.Name == "source")
            return EvalSourceFunction(fc.Args, item, paramType, ctx);

        // Built-in sink(providerName) — returns a sink handle
        if (fc.Name == "sink")
            return EvalSinkFunction(fc.Args, item, paramType, ctx);

        // Check user-defined functions first
        if (_functions.TryGetValue(fc.Name, out var funcGroup))
        {
            // For direct calls with arguments where the function has no named parameters,
            // the first arg IS the input item (e.g., text(myBool) → selects text(bool) overload).
            // For functions WITH named parameters, all args bind to params (currying semantics).
            if (fc.Args.Count > 0)
            {
                // Peek: does any overload in this group have 0 named params?
                // If so, the first arg is the input item for type-based dispatch.
                bool hasZeroParamOverload = funcGroup.Any(f => f.Parameters.Count == 0);
                if (hasZeroParamOverload)
                {
                    var firstArg = Eval(fc.Args[0], item, paramType, ctx);
                    var argType = InferValueType(firstArg) ?? paramType;
                    var func = ResolveFunction(funcGroup, argType, firstArg, ctx, callArgCount: fc.Args.Count - 1);
                    if (func.Parameters.Count == 0)
                    {
                        // The resolved overload takes no named params — first arg is the input
                        var remainingArgs = fc.Args.Count > 1 ? fc.Args.GetRange(1, fc.Args.Count - 1) : new List<Expression>();
                        return ApplyFunction(func, firstArg, remainingArgs, item, paramType, ctx);
                    }
                    // Resolved to an overload with named params — fall through to normal dispatch
                }
            }
            // Default: resolve by pipeline type, all args are named params (supports currying)
            var funcDefault = ResolveFunction(funcGroup, paramType, item, ctx, callArgCount: fc.Args.Count);
            return ApplyFunction(funcDefault, item, fc.Args, item, paramType, ctx);
        }

        // Check if the name resolves to a CopClosure (curried function)
        var closureValue = ctx.GetAncestor(fc.Name);
        if (closureValue is not CopClosure)
            closureValue = TryResolveClosure(fc.Name, item, paramType, ctx);
        if (closureValue is CopClosure closure)
            return ApplyClosure(closure, item, fc.Args, paramType, ctx);

        // Path-scoped collection: Types('path') → resolve namespace and query provider
        if (_providerQueryService is not null
            && fc.Args.Count == 1
            && fc.Args[0] is LiteralExpr { Value: string pathValue }
            && fc.Name.Length > 0 && char.IsUpper(fc.Name[0]))
        {
            var ns = _registry.ResolveCollectionNamespace(fc.Name);
            if (ns is not null)
                return _providerQueryService.Query(ns, fc.Name, pathValue);
        }

        // Fall back to built-in functions
        return CallFunction(fc.Name, fc.Args, item, paramType, ctx);
    }

    /// <summary>
    /// Evaluates the built-in Code([providers], path?) function.
    /// Returns a DataObject with a lazy field resolver that queries providers on demand.
    /// </summary>
    private object? EvalTargetedCall(CallExpr mc, object item, string paramType, EvaluationContext ctx)
    {
        // Provider namespace function call (e.g., http.Post(...)) — check before user functions
        // to prevent shadowing by user-defined functions named "Post", "Get", etc.
        if (mc.Target is IdentifierExpr nsId2 && _registry.IsProviderFunctionNamespace(nsId2.Name))
        {
            var provFunc = _registry.ResolveProviderFunction(nsId2.Name, mc.Name);
            if (provFunc is not null)
            {
                var evalArgs = mc.Args.Select(a => Eval(a, item, paramType, ctx)).ToList();
                var task = provFunc(evalArgs);
                return task.GetAwaiter().GetResult();
            }
        }

        // Package-qualified predicate/function call: packageName.symbol(args)
        if (mc.Target is IdentifierExpr pkgId)
        {
            // Package-qualified predicate
            if (_packagePredicates is not null
                && _packagePredicates.TryGetValue(pkgId.Name, out var pkgPredMap)
                && pkgPredMap.TryGetValue(mc.Name, out var pkgPredGroup))
            {
                var target = Eval(mc.Target!, item, paramType, ctx);
                // If target resolved to something other than a provider namespace, use it as the call target
                // Otherwise this is a qualified predicate call, so use item as the subject
                var subject = target is ProviderNamespaceRef ? item : target ?? item;
                var pred = ResolvePredicate(pkgPredGroup, subject, paramType, ctx);
                if (pred is not null)
                {
                    var result2 = ToBool(Eval(pred.Body, subject, pred.ParameterType, ctx));
                    return mc.Negated ? !result2 : (object)result2;
                }
            }

            // Package-qualified function
            if (_packageFunctions is not null
                && _packageFunctions.TryGetValue(pkgId.Name, out var pkgFuncMap)
                && pkgFuncMap.TryGetValue(mc.Name, out var pkgFuncGroup))
            {
                if (mc.Negated)
                    throw new InvalidOperationException($"Cannot negate function call '{mc.Name}' — functions produce values, not booleans");
                // For qualified functions, there is no "target" to pipe through — apply directly to item
                var func = ResolveFunction(pkgFuncGroup, paramType, item, ctx, callArgCount: mc.Args.Count);
                return ApplyFunction(func, item, mc.Args, item, paramType, ctx);
            }
        }

        // Check if this is a function call (transforms type, not a boolean filter)
        if (_functions.TryGetValue(mc.Name, out var funcGroup))
        {
            if (mc.Negated)
                throw new InvalidOperationException($"Cannot negate function call '{mc.Name}' — functions produce values, not booleans");
            var target = Eval(mc.Target!, item, paramType, ctx);
            if (target is null) return null;
            // Resolve overload using the target's actual type (pipe semantics)
            var targetType = InferValueType(target);
            var func = ResolveFunction(funcGroup, targetType, target, ctx, callArgCount: mc.Args.Count);
            return ApplyFunction(func, target, mc.Args, item, paramType, ctx);
        }

        // Check if this is a closure call (curried function)
        var closureVal = ctx.GetAncestor(mc.Name);
        if (closureVal is not CopClosure)
        {
            // Try resolving from let declarations
            closureVal = TryResolveClosure(mc.Name, item, paramType, ctx);
        }
        if (closureVal is CopClosure closure)
        {
            if (mc.Negated)
                throw new InvalidOperationException($"Cannot negate closure call '{mc.Name}' — closures produce values, not booleans");
            return ApplyClosure(closure, item, mc.Args, paramType, ctx);
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

        // Built-in functions accessible via colon piping: x:read => read(x)
        if (mc.Name is "read" && mc.Args.Count == 0)
        {
            if (mc.Negated)
                throw new InvalidOperationException($"Cannot negate function call '{mc.Name}' — functions produce values, not booleans");
            var target = Eval(mc.Target!, item, paramType, ctx);
            if (target is null) return null;
            return ReadFileSandboxed(target?.ToString() ?? "");
        }

        var result = CallPredicate(Eval(mc.Target!, item, paramType, ctx), mc.Name, mc.Args, item, paramType, ctx);
        return mc.Negated ? !ToBool(result) : result;
    }

    /// <summary>Evaluate a let-bound value with cycle detection.</summary>
    private object? EvalLetValue(string name, LetDeclaration decl)
    {
        if (!_evaluatingLetValues.Add(name))
            throw new InvalidOperationException($"Circular let value reference: '{name}'");
        try
        {
            var expr = decl.IsValueBinding ? decl.ValueExpression! : decl.SourceExpression!;
            // Let value expressions are context-independent (no item/paramType needed)
            var result = Eval(expr, new object(), "", new EvaluationContext());
            return ApplyTypeAnnotation(result, decl);
        }
        finally
        {
            _evaluatingLetValues.Remove(name);
        }
    }

    /// <summary>
    /// If the let declaration has a type annotation and the value is a DataObject,
    /// overrides the DataObject's TypeName to the declared type.
    /// This enables schema enforcement on subsequent property access.
    /// </summary>
    private static object? ApplyTypeAnnotation(object? value, LetDeclaration decl)
    {
        if (decl.TypeAnnotation is not null && value is DataObject ao)
            ao.TypeName = decl.TypeAnnotation;
        return value;
    }

    private object? EvalMemberAccess(MemberAccessExpr ma, object item, string paramType, EvaluationContext ctx)
    {
        // Qualified flags/enum constant: Modifier.Public, TypeKind.Class
        if (ma.Target is IdentifierExpr id)
        {
            var flagsValue = _registry.TryResolveQualifiedFlagsConstant(id.Name, ma.Member);
            if (flagsValue is not null) return flagsValue.Value;

            var enumValue = _registry.TryResolveQualifiedEnumConstant(id.Name, ma.Member);
            if (enumValue is not null) return enumValue;

            // Package-qualified let binding: packageName.letName
            if (_packageLets is not null
                && _packageLets.TryGetValue(id.Name, out var pkgLetMap)
                && pkgLetMap.TryGetValue(ma.Member, out var letDecl)
                && letDecl.IsValueBinding)
            {
                return EvalLetValue(letDecl.Name, letDecl);
            }
        }

        return GetMember(Eval(ma.Target, item, paramType, ctx), ma.Member);
    }

    private object? EvalIdentifier(string name, object item, string paramType, EvaluationContext ctx)
    {
        if (name == paramType) return item;
        if (name == "item") return item;
        if (name == "null") return null;

        // Built-in isError predicate (when used as bare identifier in filter/predicate position)
        if (name == "isError") return ErrorValue.IsError(item);

        if (_predicates.TryGetValue(name, out var group))
        {
            var pred = ResolvePredicate(group, item, paramType, ctx);
            if (pred is null) return false; // no matching overload
            return ToBool(Eval(pred.Body, item, pred.ParameterType, ctx));
        }

        // Bare function name as transform: `handle` means `handle(item)`
        if (_functions.TryGetValue(name, out var funcGroup))
        {
            var func = ResolveFunction(funcGroup, paramType, item, ctx, callArgCount: 0);
            return ApplyFunction(func, item, [], item, paramType, ctx);
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
                var result = Eval(letDecl.ValueExpression!, item, paramType, ctx);
                return ApplyTypeAnnotation(result, letDecl);
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
                    var result = Eval(letDeclExpr.SourceExpression, item, paramType, ctx);
                    return ApplyTypeAnnotation(result, letDeclExpr);
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

        // Check for ambiguous flags constant (defined in multiple flags types)
        var flagsOwners = _registry.GetFlagsMemberOwners(name);
        if (flagsOwners is not null && flagsOwners.Count > 1)
            throw new InvalidOperationException(
                $"Flags member '{name}' is ambiguous — defined in: {string.Join(", ", flagsOwners)}. " +
                $"Use qualified syntax: {flagsOwners[0]}.{name}");

        // Enum constant resolution (e.g., Class → "Class", Method → "Method")
        var enumValue = _registry.TryResolveEnumConstant(name);
        if (enumValue is not null) return enumValue;

        // Check for ambiguous enum constant (defined in multiple enum types)
        var enumOwners = _registry.GetEnumMemberOwners(name);
        if (enumOwners is not null && enumOwners.Count > 1)
            throw new InvalidOperationException(
                $"Enum member '{name}' is ambiguous — defined in: {string.Join(", ", enumOwners)}. " +
                $"Use qualified syntax: {enumOwners[0]}.{name}");

        // Provider namespace resolution (e.g., "http" → ProviderNamespaceRef for http.Get/Post/Send)
        if (_registry.IsProviderFunctionNamespace(name))
            return new ProviderNamespaceRef(name);

        // Global collection fallback: if the identifier is a registered global collection,
        // return its items. This enables predicate bodies to reference collections
        // (e.g., snippetFences:any(pred)) even when _resolvedCollections is not populated.
        if (_registry.IsGlobalCollection(name))
        {
            var globalItems = _registry.GetGlobalCollectionItems(name);
            if (globalItems is not null) return globalItems;
        }

        // Bool property resolution: if the identifier matches a bool property on the item,
        // return its value. This enables filter chains like Lines:isComment where
        // isComment is a provider-registered boolean property on the Line type.
        // Also tries PascalCase (e.g., "empty" → "Empty") since Cop properties are PascalCase.
        var itemTypeName = _registry.InferTypeName(item);
        if (itemTypeName is not null)
        {
            var typeDesc = _registry.GetType(itemTypeName);
            var propDesc = typeDesc?.GetProperty(name);
            // Try PascalCase variant if exact match fails (camelCase predicate → PascalCase property)
            if (propDesc is null && name.Length > 0 && char.IsLower(name[0]))
                propDesc = typeDesc?.GetProperty(char.ToUpperInvariant(name[0]) + name[1..]);
            if (propDesc?.Accessor is not null)
            {
                var val = propDesc.Accessor(item);
                if (val is bool) return val;
            }
        }

        // Language filter fallback: if the item has a File.Language property,
        // check if the identifier matches the language. This enables filter chains
        // like Types:csharp:client where "csharp" matches File.Language == "csharp".
        if (itemTypeName is not null)
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
                                   string.Equals(langStr, name, StringComparison.Ordinal);
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
                else
                {
                    // Bool property fallback: a bool property IS a predicate (Type → bool)
                    var itemType = _registry.InferTypeName(item);
                    if (itemType is not null)
                    {
                        var propDesc = _registry.GetType(itemType)?.GetProperty(pred.Constraint);
                        if (propDesc?.Accessor is not null && propDesc.Accessor(item) is bool boolVal && boolVal)
                            return pred;
                    }
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

        // DataObject: resolve fields by name, plus map properties
        if (target is DataObject ao)
        {
            // Built-in DataObject members (always allowed regardless of schema)
            switch (member)
            {
                case "Keys": return ao.Fields.Keys.ToList<object>();
                case "Values": return ao.Fields.Values.Where(v => v is not null).Cast<object>().ToList();
                case "Count": return ao.Fields.Count;
            }

            // Schema enforcement: if the type has declared properties, validate member access
            ValidateDataObjectSchema(ao, member);

            return ao.GetField(member);
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
                case "Rest":
                case "Tail": return list.Count > 1 ? list.Cast<object>().Skip(1).ToList() : new List<object>();
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

    /// <summary>
    /// Validates that a member access on a DataObject is allowed by the type's schema.
    /// Types with no declared properties (like 'Data') are dynamic — any access is allowed.
    /// Types with declared properties enforce that the member exists in the schema.
    /// </summary>
    private void ValidateDataObjectSchema(DataObject ao, string member)
    {
        var typeDesc = _registry.GetType(ao.TypeName);
        if (typeDesc is null) return; // unknown type → allow dynamic access

        var properties = typeDesc.GetAllProperties().ToList();
        if (properties.Count == 0) return; // empty type = dynamic (e.g., Data = {})

        // Type has declared properties — enforce schema
        if (!properties.Any(p => string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase)))
        {
            var available = string.Join(", ", properties.Select(p => p.Name));
            throw new InvalidOperationException(
                $"Property '{member}' is not defined on type '{ao.TypeName}'. Available properties: {available}");
        }
    }

    private object? CallPredicate(object? target, string predicate, List<Expression> args,
        object item, string paramType, EvaluationContext ctx)
    {
        if (target is null) return null;

        // Provider namespace function dispatch (e.g., http.Get(...), http.Post(...))
        if (target is ProviderNamespaceRef nsRef)
        {
            var func = _registry.ResolveProviderFunction(nsRef.Name, predicate);
            if (func is null)
                throw new InvalidOperationException($"Unknown function '{nsRef.Name}.{predicate}'");
            var nsArgs = args.Select(a => Eval(a, item, paramType, ctx)).ToList();
            // Provider functions are async — block synchronously here.
            // In streaming pipelines, the interpreter awaits the Task returned by Eval.
            var task = func(nsArgs);
            return task.GetAwaiter().GetResult();
        }

        // Built-in isError predicate — works on any value
        if (predicate == "isError") return ErrorValue.IsError(target);

        // Map/DataObject operations
        if (target is DataObject so)
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
            case "count":
            {
                var predExpr = args[0];
                int count = 0;
                foreach (var collItem in collection)
                {
                    if (collItem is null) continue;
                    string itemType = InferItemType(predExpr, collItem);
                    if (ToBool(Eval(predExpr, collItem, itemType, ctx)))
                        count++;
                }
                return count;
            }
            case "Where":
            case "where":
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
            case "first":
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
            case "last":
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
            case "single":
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
            case "elementAt":
            {
                var index = ToInt(Eval(args[0], item, paramType, ctx));
                return index >= 0 && index < collection.Count ? collection[index] : null;
            }
            case "Select":
            case "select":
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
            case "orderBy":
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
            case "orderByDescending":
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
            case "sum":
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
            case "min":
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
            case "max":
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
            case "average":
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
            case "distinct":
            {
                if (args.Count > 0)
                {
                    // Distinct by expression: deduplicate by projected value
                    var fieldExpr = args[0];
                    var seen = new HashSet<string>(StringComparer.Ordinal);
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
                    var seen = new HashSet<string>(StringComparer.Ordinal);
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
            case "groupBy":
            {
                var fieldExpr = args[0];
                var groups = new Dictionary<string, List<object>>(StringComparer.Ordinal);
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
                // Return as list of DataObjects with Key and Items properties
                var result = new List<object>();
                foreach (var key in groupOrder)
                {
                    var groupObj = new DataObject("Group");
                    groupObj.Set("Key", key);
                    groupObj.Set("Items", groups[key]);
                    groupObj.Set("Count", groups[key].Count);
                    result.Add(groupObj);
                }
                return result;
            }
            case "containsAny":
            {
                // Check if the collection contains any element from the argument list
                var argVal = Eval(args[0], item, paramType, ctx);
                if (argVal is IList argList)
                {
                    var argSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in argList)
                        if (a is not null) argSet.Add(a.ToString()!);
                    foreach (var collItem in collection)
                        if (collItem is not null && argSet.Contains(collItem.ToString()!))
                            return true;
                    return false;
                }
                // Single value fallback
                var singleVal = argVal?.ToString();
                foreach (var collItem in collection)
                    if (string.Equals(collItem?.ToString(), singleVal, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            case "Reduce":
            case "reduce":
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
            case "push":
            {
                // Immutable append: returns new list with item at end
                var value = Eval(args[0], item, paramType, ctx);
                var result = new List<object>(collection.Cast<object>());
                if (value is not null) result.Add(value);
                return result;
            }
            case "prepend":
            {
                // Immutable prepend: returns new list with item at front
                var value = Eval(args[0], item, paramType, ctx);
                var result = new List<object>();
                if (value is not null) result.Add(value);
                result.AddRange(collection.Cast<object>());
                return result;
            }
            case "pop":
            {
                // Immutable pop: returns new list without last element
                var result = collection.Cast<object>().ToList();
                if (result.Count > 0) result.RemoveAt(result.Count - 1);
                return result;
            }
            case "concat":
            {
                // Immutable concat: returns new list combining both
                var other = Eval(args[0], item, paramType, ctx);
                var result = new List<object>(collection.Cast<object>());
                if (other is IList otherList)
                {
                    foreach (var el in otherList)
                        if (el is not null) result.Add(el);
                }
                else if (other is not null)
                {
                    result.Add(other);
                }
                return result;
            }
            default:
            {
                // User-defined predicate as filter: collection:predName → filter items
                if (_predicates.TryGetValue(predicate, out var predGroup))
                {
                    var result = new List<object>();
                    foreach (var collItem in collection)
                    {
                        if (collItem is null) continue;
                        string itemType = InferItemType(new IdentifierExpr(predicate), collItem);
                        var pred = ResolvePredicate(predGroup, collItem, itemType, ctx);
                        if (pred is null) continue;
                        var (pass, _) = EvaluateAsBool(pred.Body, collItem, pred.ParameterType);
                        if (pass) result.Add(collItem);
                    }
                    return result;
                }
                throw new InvalidOperationException($"Unknown collection predicate '{predicate}'");
            }
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
            case "read":
            {
                var path = Eval(args[0], item, paramType, ctx)?.ToString() ?? "";
                return ReadFileSandboxed(path);
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

    /// <summary>
    /// Infers the cop type name from a runtime value for pipe-based overload resolution.
    /// </summary>
    private string? InferValueType(object? value) => value switch
    {
        null => null,
        string => "string",
        int or long => "int",
        double or float => "number",
        bool => "bool",
        byte[] => "bytes",
        _ => _registry.InferTypeName(value)
    };

    /// <summary>
    /// Dispatches intrinsic function calls declared with '= intrinsic' in .cop files.
    /// </summary>
    private object? CallIntrinsicFunction(string name, object? inputItem, Dictionary<string, object?> paramBindings)
    {
        return name switch
        {
            "text" => inputItem switch
            {
                int i => i.ToString(),
                long l => l.ToString(),
                byte b => b.ToString(),
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                _ => throw new InvalidOperationException($"No intrinsic text() overload for type '{inputItem?.GetType().Name ?? "null"}'")
            },
            "read" => ReadFileSandboxed(inputItem?.ToString() ?? ""),
            "error" => inputItem is string msg
                ? new ErrorValue(msg)
                : new ErrorValue(inputItem?.ToString()),
            "pathMatches" => GlobMatch(
                paramBindings.TryGetValue("path", out var pm) ? pm?.ToString() ?? "" : "",
                paramBindings.TryGetValue("pattern", out var pp) ? pp?.ToString() ?? "" : ""),
            _ => throw new InvalidOperationException($"Unknown intrinsic function: '{name}'")
        };
    }

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
    /// Apply a function definition to an item, producing an DataObject.
    /// Evaluates each field mapping expression with the item as context,
    /// binding function parameters from the provided arguments.
    /// </summary>
    private object? ApplyFunction(FunctionDefinition func, object? target, List<Expression> callArgs,
        object item, string paramType, EvaluationContext ctx)
    {
        // Currying: if fewer args than parameters, return a closure
        if (callArgs.Count < func.Parameters.Count)
        {
            var boundArgs = new List<object?>();
            for (int i = 0; i < callArgs.Count; i++)
                boundArgs.Add(Eval(callArgs[i], item, paramType, ctx));
            return new CopClosure(func, boundArgs);
        }

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

        // Intrinsic function: dispatch to built-in C# implementation
        if (func.IsIntrinsic)
        {
            return CallIntrinsicFunction(func.Name, inputItem, paramBindings);
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

        // Record-body function: evaluate field mappings and return DataObject
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

        return new DataObject(func.ReturnType, fields);
    }

    /// <summary>
    /// Apply a closure (partially-applied function) with additional arguments.
    /// Concatenates the bound args with the new args and calls the underlying function.
    /// If still not enough args, returns a new closure with more args bound.
    /// </summary>
    private object? ApplyClosure(CopClosure closure, object item, List<Expression> newArgs,
        string paramType, EvaluationContext ctx)
    {
        var allArgs = new List<Expression>();
        // Convert bound args to literal expressions
        foreach (var bound in closure.BoundArgs)
            allArgs.Add(new LiteralExpr(bound));
        allArgs.AddRange(newArgs);

        return ApplyFunction(closure.Function, null, allArgs, item, paramType, ctx);
    }

    /// <summary>
    /// Try to resolve a name to a CopClosure via let declarations.
    /// Returns null if not found or if the let value doesn't evaluate to a closure.
    /// </summary>
    private object? TryResolveClosure(string name, object item, string paramType, EvaluationContext ctx)
    {
        if (_letDeclarations is not null &&
            _letDeclarations.TryGetValue(name, out var letDecl) &&
            letDecl.IsValueBinding)
        {
            if (!_evaluatingLetValues.Add(name))
                return null;
            try
            {
                return Eval(letDecl.ValueExpression!, item, paramType, ctx);
            }
            finally
            {
                _evaluatingLetValues.Remove(name);
            }
        }
        return null;
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

        var result = Eval(expr, item, paramType, ctx);

        // Resolve string templates with function parameters (e.g., '{a}{b}' → parameter values)
        if (result is string str && str.Contains('{'))
        {
            var segments = TemplateParser.Parse(str);
            var sb = new System.Text.StringBuilder();
            foreach (var segment in segments)
            {
                if (segment is LiteralSegment lit)
                    sb.Append(lit.Text);
                else if (segment is ExpressionSegment exprSeg)
                {
                    var path = exprSeg.PropertyPath;
                    // Try parameter bindings first (single name like {a})
                    if (path.Length == 1 && paramBindings.TryGetValue(path[0], out var paramVal))
                        sb.Append(paramVal?.ToString() ?? "");
                    else
                    {
                        // Try resolving via context/item property path
                        sb.Append(ResolveExprSegment(path, item, paramType, ctx));
                    }
                }
                else if (segment is AnnotatedLiteralSegment annLit)
                    sb.Append(annLit.Text);
            }
            return sb.ToString();
        }

        return result;
    }

    private string ResolveExprSegment(string[] path, object item, string paramType, EvaluationContext ctx)
    {
        // Handle dotted expressions like item.Name
        var root = path[0];
        var target = root == "item" ? item : ctx.GetAncestor(root) ?? item;
        if (path.Length > 1 && target is not null)
        {
            for (int i = 1; i < path.Length; i++)
            {
                if (target is null) return $"{{{string.Join(".", path)}}}";
                var typeName = _registry.InferTypeName(target);
                if (typeName is not null)
                {
                    var prop = _registry.GetType(typeName)?.GetProperty(path[i]);
                    if (prop?.Accessor is not null)
                    {
                        target = prop.Accessor(target);
                        continue;
                    }
                }
                if (target is DataObject so && so.Fields.TryGetValue(path[i], out var fieldVal))
                {
                    target = fieldVal;
                    continue;
                }
                return $"{{{string.Join(".", path)}}}"; // leave unresolved
            }
        }
        return target?.ToString() ?? "";
    }

    /// <summary>
    /// Check if a name refers to a function definition.
    /// </summary>
    public bool IsFunction(string name) => _functions.ContainsKey(name);

    /// <summary>
    /// Check if a let declaration resolves to a closure (partially-applied function).
    /// </summary>
    public bool IsClosureLet(string name)
    {
        if (_letDeclarations is null || !_letDeclarations.TryGetValue(name, out var letDecl))
            return false;
        if (!letDecl.IsValueBinding || letDecl.ValueExpression is not CallExpr fc)
            return false;
        // It's a closure if the function call name maps to a known function
        return _functions.ContainsKey(fc.Name);
    }

    /// <summary>
    /// Evaluates a let declaration's value expression and returns the result.
    /// Used to resolve let bindings that may hold intrinsic objects (streaming sources, sinks, etc.).
    /// </summary>
    public object? EvaluateLetValue(LetDeclaration letDecl)
    {
        if (!letDecl.IsValueBinding || letDecl.ValueExpression is null)
            return null;

        var ctx = new EvaluationContext();
        var dummy = new object();
        return Eval(letDecl.ValueExpression, dummy, "item", ctx);
    }

    /// <summary>
    /// Apply a closure (from a let binding) as a transform filter on an item.
    /// Resolves the let value to a CopClosure, then applies remaining args + item.
    /// </summary>
    public object? ApplyClosureFilter(string name, object item, string itemType, List<Expression> args)
    {
        var ctx = new EvaluationContext();
        var closureValue = TryResolveClosure(name, item, itemType, ctx);
        if (closureValue is CopClosure closure)
            return ApplyClosure(closure, item, args, itemType, ctx);
        return item; // fallback: pass through unchanged
    }

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
    /// Apply a function to an item, producing an DataObject.
    /// Used by the interpreter for map operations in collection chains.
    /// </summary>
    public object? ApplyFunction(string funcName, object item, string itemType, List<Expression> args)
    {
        if (!_functions.TryGetValue(funcName, out var group))
            throw new InvalidOperationException($"Unknown function '{funcName}'");
        var ctx = new EvaluationContext();
        var func = ResolveFunction(group, itemType, item, ctx);
        return ApplyFunction(func, item, args, item, itemType, ctx);
    }

    /// <summary>
    /// Resolve a function overload by matching constraints and input type.
    /// Constrained overloads are evaluated first; unconstrained is fallback.
    /// </summary>
    private FunctionDefinition ResolveFunction(List<FunctionDefinition> group, string? inputType, object? item = null, EvaluationContext? ctx = null, int? callArgCount = null)
    {
        // If we have an item and context, try constrained overloads first
        if (item is not null && ctx is not null)
        {
            foreach (var func in group)
            {
                if (func.Constraint is null) continue;
                if (func.InputType != inputType && inputType is not null
                    && group.Any(f => f.InputType == inputType && f.Constraint is null))
                {
                    // Skip constraints for wrong type if there's a type-matched unconstrained overload
                    if (func.InputType != inputType) continue;
                }
                try
                {
                    var result = Eval(func.Constraint, item, func.InputType, ctx);
                    if (ToBool(result)) return func;
                }
                catch { /* constraint evaluation failed, skip */ }
            }
        }

        // Fall back to type match or first definition
        if (inputType != null)
        {
            // When call arg count is known, prefer overload with matching parameter count
            if (callArgCount is not null)
            {
                var paramMatch = group.FirstOrDefault(f => f.InputType == inputType && f.Constraint is null && f.Parameters.Count == callArgCount.Value);
                if (paramMatch != null) return paramMatch;
            }
            var match = group.FirstOrDefault(f => f.InputType == inputType && f.Constraint is null);
            if (match != null) return match;

            // When all arguments are provided explicitly (callArgCount matches parameter count),
            // the function doesn't depend on the pipeline item type — dispatch by param count.
            if (callArgCount is not null)
            {
                var paramCountMatch = group.FirstOrDefault(f => f.Parameters.Count == callArgCount.Value && f.Constraint is null);
                if (paramCountMatch != null) return paramCountMatch;
            }
        }

        // Strict: if all overloads have type-specific input types that differ from the
        // requested type, throw a clear error. This prevents calling text(Widget) when
        // only text(string), text(bool), text(int), text(bytes) exist.
        if (inputType != null && group.All(f => f.InputType != null && f.InputType != inputType))
        {
            var availableTypes = string.Join(", ", group.Select(f => f.InputType).Distinct());
            var funcName = group[0].Name;
            throw new InvalidOperationException(
                $"No overload of '{funcName}' accepts type '{inputType}'. Available overloads: {funcName}({availableTypes})");
        }

        // Last resort: first unconstrained, then first overall
        return group.FirstOrDefault(f => f.Constraint is null) ?? group[0];
    }

    /// <summary>
    /// Creates a DataObject representing a Code([providers], path?) result.
    /// The object has a lazy field resolver that queries providers on demand and memoizes results.
    /// </summary>
    private DataObject CreateCodeObject(string[] providers, string? path, string typeName = "Codebase")
    {
        var registry = _registry;
        var queryService = _providerQueryService;

        var obj = new DataObject(typeName);
        obj.WithFieldResolver(collectionName =>
        {
            var results = new List<object>();
            foreach (var provider in providers)
            {
                if (path is not null && queryService is not null)
                {
                    var items = queryService.Query(provider, collectionName, path);
                    results.AddRange(items);
                }
                else
                {
                    var qualified = $"{provider}.{collectionName}";
                    var items = registry.GetGlobalCollectionItems(qualified);
                    if (items is not null)
                        results.AddRange(items);
                }
            }
            return results;
        });
        return obj;
    }

    /// <summary>
    /// Evaluates the built-in data(providerName) function.
    /// Returns a DataObject with a lazy field resolver for any provider.
    /// </summary>
    private object EvalDataFunction(List<Expression> args, object item, string paramType, EvaluationContext ctx)
    {
        if (args.Count != 1)
            throw new InvalidOperationException("data() requires exactly 1 argument: data('providerName')");

        var nameVal = Eval(args[0], item, paramType, ctx);
        var providerName = nameVal?.ToString()
            ?? throw new InvalidOperationException("data() argument must be a string");

        return CreateCodeObject([providerName], path: null, typeName: "Data");
    }

    /// <summary>
    /// Evaluates the built-in source(providerName) function.
    /// Returns the streaming source handle registered under the provider's namespace.
    /// </summary>
    private object EvalSourceFunction(List<Expression> args, object item, string paramType, EvaluationContext ctx)
    {
        if (args.Count != 1)
            throw new InvalidOperationException("source() requires exactly 1 argument: source('providerName')");

        var nameVal = Eval(args[0], item, paramType, ctx);
        var providerName = nameVal?.ToString()
            ?? throw new InvalidOperationException("source() argument must be a string");

        // Find the first streaming source registered under this provider namespace
        var qualifiedNames = _registry.GetStreamingSourceNames();
        var prefix = providerName + ".";
        foreach (var name in qualifiedNames)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var source = _registry.ResolveStreamingSource(name);
                if (source is not null)
                    return source;
            }
        }

        throw new InvalidOperationException($"No streaming source found for provider '{providerName}'. Ensure the provider package is imported.");
    }

    /// <summary>
    /// Evaluates the built-in sink(providerName) function.
    /// Returns the sink handle registered under the provider's namespace.
    /// </summary>
    private object EvalSinkFunction(List<Expression> args, object item, string paramType, EvaluationContext ctx)
    {
        if (args.Count != 1)
            throw new InvalidOperationException("sink() requires exactly 1 argument: sink('providerName')");

        var nameVal = Eval(args[0], item, paramType, ctx);
        var providerName = nameVal?.ToString()
            ?? throw new InvalidOperationException("sink() argument must be a string");

        // Find the first sink registered under this provider namespace
        var sinks = _registry.GetNamespaceSinks(providerName);
        if (sinks is not null && sinks.Count > 0)
            return sinks.Values.First();

        throw new InvalidOperationException($"No sink found for provider '{providerName}'. Ensure the provider package is imported.");
    }
}