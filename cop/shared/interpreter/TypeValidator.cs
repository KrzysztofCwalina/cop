namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Runtime type validation for function signatures.
/// Validates argument types, arity, and return types against declared signatures.
/// </summary>
public static class TypeValidator
{
    /// <summary>
    /// Validates arguments before function body dispatch.
    /// Checks arity and parameter types. For generic functions, infers type bindings
    /// and validates against substituted types.
    /// </summary>
    public static void ValidateArguments(FunctionDecl decl, IReadOnlyList<CopValue> args, TypeRegistry? registry = null)
    {
        // Arity check: too many arguments (unless last param is a collection = param array)
        if (args.Count > decl.Params.Count && decl.Params.Count > 0)
        {
            var lastParam = decl.Params[^1];
            if (lastParam.Type is null || !lastParam.Type.IsCollection)
            {
                throw new CopEvaluationException(
                    $"'{decl.Name}' expects {decl.Params.Count} argument(s), got {args.Count}",
                    decl.Line);
            }
        }

        // For generic functions, infer bindings and validate with substituted types
        if (GenericInference.HasTypeParameters(decl))
        {
            ValidateGenericArguments(decl, args, registry);
            return;
        }

        // Non-generic: standard parameter type checks
        var isParamArray = decl.Params.Count > 0 && decl.Params[^1].Type is { IsCollection: true };
        for (int i = 0; i < args.Count && i < decl.Params.Count; i++)
        {
            var param = decl.Params[i];
            if (param.Type is null) continue; // untyped parameter — no check

            // Skip type check for last param when acting as param array (individual args will be collected)
            if (isParamArray && i == decl.Params.Count - 1 && args.Count >= decl.Params.Count)
                continue;

            var arg = ForceValue(args[i]);
            if (!IsCompatible(arg, param.Type, registry))
            {
                throw new CopEvaluationException(
                    $"'{decl.Name}' parameter '{param.Name}' expects {FormatTypeRef(param.Type)}, " +
                    $"got {GetActualTypeName(arg)}",
                    decl.Line);
            }
        }
    }

    /// <summary>
    /// Validates arguments for a generic function using inferred type bindings.
    /// Substitutes type variables with inferred concrete types before checking compatibility.
    /// </summary>
    private static void ValidateGenericArguments(FunctionDecl decl, IReadOnlyList<CopValue> args, TypeRegistry? registry)
    {
        var bindings = GenericInference.InferBindings(decl, args);

        // Validate trait constraints if a registry is available
        if (registry is not null)
        {
            var constraintError = GenericInference.ValidateConstraints(decl, bindings, registry);
            if (constraintError is not null)
                throw new CopEvaluationException(constraintError, decl.Line);
        }

        for (int i = 0; i < args.Count && i < decl.Params.Count; i++)
        {
            var param = decl.Params[i];
            if (param.Type is null) continue;

            // Skip callable/lambda parameters — validated at invocation time
            var substituted = GenericInference.SubstituteTypeRef(param.Type, bindings);
            if (substituted.Name.Contains("=>")) continue;
            if (substituted.Name is "lambda" or "function") continue;

            var arg = ForceValue(args[i]);
            // Skip lambda values — they're validated at invocation time
            if (arg is ICopCallable) continue;

            if (!IsCompatible(arg, substituted))
            {
                throw new CopEvaluationException(
                    $"'{decl.Name}' parameter '{param.Name}' expects {FormatTypeRef(substituted)}, " +
                    $"got {GetActualTypeName(arg)}",
                    decl.Line);
            }
        }
    }

    /// <summary>
    /// Validates the return value after function body execution.
    /// Only checks if the function has a declared return type.
    /// For generic functions, infers the concrete return type from type bindings.
    /// </summary>
    public static void ValidateReturn(FunctionDecl decl, CopValue result)
    {
        if (decl.ReturnType is null) return; // no declared return type — no check

        // Skip validation for generic return types — inference handles consistency
        if (GenericInference.IsTypeVariable(decl.ReturnType.Name))
            return;

        // For non-generic return types, check as before
        if (decl.ReturnType.IsCollection && GenericInference.IsTypeVariable(decl.ReturnType.Name))
            return;

        var value = ForceValue(result);
        if (!IsCompatible(value, decl.ReturnType))
        {
            throw new CopEvaluationException(
                $"'{decl.Name}' declares return type {FormatTypeRef(decl.ReturnType)}, " +
                $"but returned {GetActualTypeName(value)}",
                decl.Line);
        }
    }

    /// <summary>
    /// Checks if a runtime value is compatible with a declared type reference.
    /// </summary>
    public static bool IsCompatible(CopValue value, TypeRef typeRef, TypeRegistry? registry = null)
    {
        // Type variables are always compatible (validated through inference)
        if (GenericInference.IsTypeVariable(typeRef.Name))
            return true;

        // Function type signatures are compatible with callables
        if (typeRef.Name.Contains("=>"))
            return value is ICopCallable;

        // Collection type: [T]
        if (typeRef.IsCollection)
            return value is CopList or CopLazyCollection or CopQueryable;

        // Primitive and named types
        return typeRef.Name.ToLowerInvariant() switch
        {
            "object" => true, // top type — accepts anything including null
            "string" => value is CopString,
            "int" => value is CopInt,
            "float" => value is CopInt or CopNumber,
            "bool" => value is CopBool,
            "bytes" => value is CopObject obj && obj.TypeName == "bytes",
            "lambda" or "function" => value is ICopCallable,
            "collection" => value is CopList or CopLazyCollection or CopQueryable,
            _ => IsNamedTypeCompatible(value, typeRef.Name, registry)
        };
    }

    /// <summary>
    /// Gets a human-readable type name for a runtime value.
    /// </summary>
    public static string GetActualTypeName(CopValue value) => value switch
    {
        CopNull => "null",
        CopString => "string",
        CopInt => "int",
        CopNumber => "float",
        CopBool => "bool",
        CopList => "collection",
        CopLazyCollection => "collection",
        CopQueryable => "collection",
        CopObject obj => obj.TypeName ?? "object",
        CopDynamicObject dyn => dyn.TypeName ?? "object",
        CopProviderProxy proxy => $"provider({proxy.ProviderName})",
        CopFunction fn => $"function({fn.Declaration.Name})",
        CopFunctionGroup fg => $"function-group",
        CopLambda => "lambda",
        CopExternalFunction ext => $"extern({ext.Name})",
        _ => value.GetType().Name
    };

    /// <summary>
    /// Forces thunks to their concrete values for type checking.
    /// </summary>
    private static CopValue ForceValue(CopValue value)
    {
        while (value is CopThunk thunk)
            value = thunk.Force();
        return value;
    }

    /// <summary>
    /// Checks compatibility with a named (non-primitive) type.
    /// Matches CopObject.TypeName or CopDynamicObject.TypeName.
    /// Also checks trait conformance if a registry is provided.
    /// </summary>
    private static bool IsNamedTypeCompatible(CopValue value, string typeName, TypeRegistry? registry = null)
    {
        // CopNull is NOT compatible with named types
        if (value is CopNull) return false;

        // CopObject with matching TypeName
        if (value is CopObject obj)
        {
            if (string.Equals(obj.TypeName, typeName, StringComparison.OrdinalIgnoreCase))
                return true;
            // Check trait conformance
            if (registry is not null && obj.TypeName is not null && registry.ConformsTo(obj.TypeName, typeName))
                return true;
            return false;
        }

        // CopDynamicObject (provider-backed) with matching TypeName
        if (value is CopDynamicObject dyn)
        {
            if (string.Equals(dyn.TypeName, typeName, StringComparison.OrdinalIgnoreCase))
                return true;
            // Check trait conformance
            if (registry is not null && dyn.TypeName is not null && registry.ConformsTo(dyn.TypeName, typeName))
                return true;
            return false;
        }

        // CopProviderProxy matches 'object' (already handled above) but not named types
        return false;
    }

    private static string FormatTypeRef(TypeRef typeRef)
        => typeRef.IsCollection ? $"[{typeRef.Name}]" : typeRef.Name;
}
