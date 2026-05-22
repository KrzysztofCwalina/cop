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
    /// Checks arity and parameter types.
    /// </summary>
    public static void ValidateArguments(FunctionDecl decl, IReadOnlyList<CopValue> args)
    {
        // Arity check: too many arguments
        if (args.Count > decl.Params.Count && decl.Params.Count > 0)
        {
            throw new CopEvaluationException(
                $"'{decl.Name}' expects {decl.Params.Count} argument(s), got {args.Count}",
                decl.Line);
        }

        // Parameter type checks
        for (int i = 0; i < args.Count && i < decl.Params.Count; i++)
        {
            var param = decl.Params[i];
            if (param.Type is null) continue; // untyped parameter — no check

            var arg = ForceValue(args[i]);
            if (!IsCompatible(arg, param.Type))
            {
                throw new CopEvaluationException(
                    $"'{decl.Name}' parameter '{param.Name}' expects {FormatTypeRef(param.Type)}, " +
                    $"got {GetActualTypeName(arg)}",
                    decl.Line);
            }
        }
    }

    /// <summary>
    /// Validates the return value after function body execution.
    /// Only checks if the function has a declared return type.
    /// </summary>
    public static void ValidateReturn(FunctionDecl decl, CopValue result)
    {
        if (decl.ReturnType is null) return; // no declared return type — no check

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
    public static bool IsCompatible(CopValue value, TypeRef typeRef)
    {
        // Collection type: [T]
        if (typeRef.IsCollection)
            return value is CopList or CopLazyCollection or CopQueryableCollection;

        // Primitive and named types
        return typeRef.Name.ToLowerInvariant() switch
        {
            "object" => true, // top type — accepts anything including null
            "string" => value is CopString,
            "int" => value is CopInt,
            "number" => value is CopInt or CopNumber,
            "bool" => value is CopBool,
            "bytes" => value is CopObject obj && obj.TypeName == "bytes",
            "lambda" or "function" => value is ICopCallable,
            "collection" => value is CopList or CopLazyCollection or CopQueryableCollection,
            _ => IsNamedTypeCompatible(value, typeRef.Name)
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
        CopNumber => "number",
        CopBool => "bool",
        CopList => "collection",
        CopLazyCollection => "collection",
        CopQueryableCollection => "collection",
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
    /// </summary>
    private static bool IsNamedTypeCompatible(CopValue value, string typeName)
    {
        // CopNull is NOT compatible with named types
        if (value is CopNull) return false;

        // CopObject with matching TypeName
        if (value is CopObject obj)
            return string.Equals(obj.TypeName, typeName, StringComparison.OrdinalIgnoreCase);

        // CopDynamicObject (provider-backed) with matching TypeName
        if (value is CopDynamicObject dyn)
            return string.Equals(dyn.TypeName, typeName, StringComparison.OrdinalIgnoreCase);

        // CopProviderProxy matches 'object' (already handled above) but not named types
        return false;
    }

    private static string FormatTypeRef(TypeRef typeRef)
        => typeRef.IsCollection ? $"[{typeRef.Name}]" : typeRef.Name;
}
