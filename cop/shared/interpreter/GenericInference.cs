namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Provides generic type inference for functions with type variables.
/// Convention: single uppercase letters (A-Z) that don't resolve to known types are type variables.
/// At call sites, infers concrete type bindings from actual argument values.
/// </summary>
public static class GenericInference
{
    /// <summary>
    /// Returns true if the given type name is a type variable (single uppercase letter A-Z).
    /// </summary>
    public static bool IsTypeVariable(string name)
        => name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z';

    /// <summary>
    /// Returns true if the function declaration has any type variables in its signature.
    /// </summary>
    public static bool HasTypeParameters(FunctionDecl decl)
    {
        // Check return type
        if (decl.ReturnType is not null && ContainsTypeVariable(decl.ReturnType))
            return true;

        // Check parameters
        foreach (var param in decl.Params)
        {
            if (param.Type is not null && ContainsTypeVariable(param.Type))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Infers concrete type bindings from actual argument values matched against declared parameter types.
    /// Returns a mapping from type variable names to concrete type names.
    /// </summary>
    public static Dictionary<string, string> InferBindings(FunctionDecl decl, IReadOnlyList<CopValue> args)
    {
        var bindings = new Dictionary<string, string>();

        for (int i = 0; i < decl.Params.Count && i < args.Count; i++)
        {
            var param = decl.Params[i];
            if (param.Type is null) continue;

            var arg = ForceValue(args[i]);
            UnifyType(param.Type, arg, bindings);
        }

        return bindings;
    }

    /// <summary>
    /// Validates that inferred type bindings satisfy any declared constraints.
    /// Returns null if all constraints pass, or an error message if a constraint is violated.
    /// </summary>
    public static string? ValidateConstraints(FunctionDecl decl, Dictionary<string, string> bindings, TypeRegistry registry)
    {
        foreach (var param in decl.Params)
        {
            if (param.Type is null || param.Type.Constraint is null) continue;

            var typeVar = param.Type.Name;
            var constraint = param.Type.Constraint;

            if (!bindings.TryGetValue(typeVar, out var concreteType))
                continue; // unresolved — can't check

            if (!registry.ConformsTo(concreteType, constraint))
                return $"'{decl.Name}' requires {typeVar} to satisfy '{constraint}', but '{concreteType}' does not declare conformance to '{constraint}'";
        }

        return null;
    }

    /// <summary>
    /// Substitutes type variables in a TypeRef with their inferred concrete types.
    /// Returns a new TypeRef with type variables replaced, or the original if no substitution needed.
    /// </summary>
    public static TypeRef SubstituteTypeRef(TypeRef typeRef, IReadOnlyDictionary<string, string> bindings)
    {
        if (typeRef.IsCollection)
        {
            // [T] → [int] if T=int
            if (IsTypeVariable(typeRef.Name) && bindings.TryGetValue(typeRef.Name, out var bound))
                return new TypeRef(bound, true, typeRef.Line);
            return typeRef;
        }

        // Simple type variable: T → int
        if (IsTypeVariable(typeRef.Name) && bindings.TryGetValue(typeRef.Name, out var boundType))
            return new TypeRef(boundType, false, typeRef.Line);

        // Function type signature: (R, T) => R — substitute within the string representation
        if (typeRef.Name.Contains("=>"))
            return new TypeRef(SubstituteFunctionType(typeRef.Name, bindings), false, typeRef.Line);

        return typeRef;
    }

    /// <summary>
    /// Resolves the return type of a generic function given inferred bindings.
    /// Returns the concrete type name (e.g., "int") or null if no return type declared.
    /// </summary>
    public static string? ResolveReturnType(FunctionDecl decl, IReadOnlyDictionary<string, string> bindings)
    {
        if (decl.ReturnType is null) return null;

        var resolved = SubstituteTypeRef(decl.ReturnType, bindings);
        return resolved.IsCollection ? $"[{resolved.Name}]" : resolved.Name;
    }

    /// <summary>
    /// Gets the runtime type name of a CopValue for inference purposes.
    /// </summary>
    public static string GetValueTypeName(CopValue value) => value switch
    {
        CopNull => "object",
        CopString => "string",
        CopInt => "int",
        CopNumber => "float",
        CopBool => "bool",
        CopList => "collection",
        CopLazyCollection => "collection",
        CopQueryable => "collection",
        CopObject obj => obj.TypeName ?? "object",
        CopDynamicObject dyn => dyn.TypeName ?? "object",
        _ => "object"
    };

    /// <summary>
    /// Gets the element type of a collection by inspecting its first item.
    /// Returns "object" if the collection is empty or element type cannot be determined.
    /// </summary>
    public static string GetCollectionElementType(CopValue collection)
    {
        var items = GetFirstItem(collection);
        return items is not null ? GetValueTypeName(items) : "object";
    }

    private static bool ContainsTypeVariable(TypeRef typeRef)
    {
        if (IsTypeVariable(typeRef.Name))
            return true;

        // Check function type signatures like (R, T) => R
        if (typeRef.Name.Contains("=>"))
            return ContainsFunctionTypeVariable(typeRef.Name);

        return false;
    }

    private static bool ContainsFunctionTypeVariable(string signature)
    {
        // Scan for single uppercase letters that are type variables
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c >= 'A' && c <= 'Z')
            {
                // Check it's a standalone letter (not part of a longer word)
                bool prevBoundary = i == 0 || !char.IsLetterOrDigit(signature[i - 1]);
                bool nextBoundary = i == signature.Length - 1 || !char.IsLetterOrDigit(signature[i + 1]);
                if (prevBoundary && nextBoundary)
                    return true;
            }
        }
        return false;
    }

    private static void UnifyType(TypeRef declaredType, CopValue actualValue, Dictionary<string, string> bindings)
    {
        if (declaredType.IsCollection)
        {
            // [T] matched against a collection → infer T from element type
            if (IsTypeVariable(declaredType.Name))
            {
                var elementType = GetCollectionElementType(actualValue);
                TryBind(declaredType.Name, elementType, bindings);
            }
            return;
        }

        // Simple type variable: T matched against a value
        if (IsTypeVariable(declaredType.Name))
        {
            var typeName = GetValueTypeName(actualValue);
            TryBind(declaredType.Name, typeName, bindings);
            return;
        }

        // Function type: (R, T) => R — skip (inferred from other params)
        // We don't infer from lambda arguments since they're untyped at parse time
    }

    private static void TryBind(string typeVar, string concreteType, Dictionary<string, string> bindings)
    {
        if (bindings.TryGetValue(typeVar, out var existing))
        {
            // Already bound — check consistency (first binding wins if conflict)
            // If existing is "object" (from empty collection), allow refinement
            if (existing == "object" && concreteType != "object")
                bindings[typeVar] = concreteType;
        }
        else
        {
            bindings[typeVar] = concreteType;
        }
    }

    private static string SubstituteFunctionType(string signature, IReadOnlyDictionary<string, string> bindings)
    {
        // Replace type variables in function signatures like "(R, T) => R"
        var result = signature;
        foreach (var (typeVar, concreteType) in bindings)
        {
            // Replace standalone type variable occurrences
            result = ReplaceTypeVariable(result, typeVar, concreteType);
        }
        return result;
    }

    private static string ReplaceTypeVariable(string text, string typeVar, string replacement)
    {
        // Replace single-char type variables at word boundaries
        var result = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == typeVar[0])
            {
                bool prevBoundary = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                bool nextBoundary = i == text.Length - 1 || !char.IsLetterOrDigit(text[i + 1]);
                if (prevBoundary && nextBoundary)
                {
                    result.Append(replacement);
                    continue;
                }
            }
            result.Append(text[i]);
        }
        return result.ToString();
    }

    private static CopValue? GetFirstItem(CopValue collection)
    {
        if (collection is CopList list && list.Items.Count > 0)
            return ForceValue(list.Items[0]);

        if (collection is CopLazyCollection lazy)
        {
            foreach (var item in lazy.Enumerate())
                return ForceValue(item);
        }

        return null;
    }

    private static CopValue ForceValue(CopValue value)
    {
        while (value is CopThunk thunk)
            value = thunk.Force();
        return value;
    }
}
