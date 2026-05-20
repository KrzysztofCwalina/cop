namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

// ============================================================================
// Symbol Kinds
// ============================================================================

public enum SymbolKind
{
    Variable,
    Parameter,
    Function,
    Type,
    Enum,
    EnumMember,
    Module
}

/// <summary>
/// Distinguishes different callable flavors for downstream phases.
/// </summary>
public enum CallableKind
{
    Function,
    Predicate,
    Command,
    External
}

// ============================================================================
// Symbol Hierarchy
// ============================================================================

/// <summary>
/// A named entity in the program: variable, function, type, etc.
/// Symbols are produced by the binder and stored in scopes.
/// </summary>
public abstract class Symbol
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public bool IsExported { get; init; }
    public int DeclarationLine { get; init; }

    protected Symbol(string name, SymbolKind kind)
    {
        Name = name;
        Kind = kind;
    }
}

/// <summary>
/// A local or top-level variable introduced by `let`.
/// </summary>
public sealed class VariableSymbol : Symbol
{
    public TypeRef? DeclaredType { get; }
    public bool IsReadOnly { get; }

    public VariableSymbol(string name, TypeRef? declaredType = null, bool isReadOnly = true)
        : base(name, SymbolKind.Variable)
    {
        DeclaredType = declaredType;
        IsReadOnly = isReadOnly;
    }
}

/// <summary>
/// A function parameter.
/// </summary>
public sealed class ParameterSymbol : Symbol
{
    public TypeRef? DeclaredType { get; }
    public int Ordinal { get; }

    public ParameterSymbol(string name, TypeRef? declaredType, int ordinal)
        : base(name, SymbolKind.Parameter)
    {
        DeclaredType = declaredType;
        Ordinal = ordinal;
    }
}

/// <summary>
/// A function, predicate, or command.
/// </summary>
public sealed class FunctionSymbol : Symbol
{
    public CallableKind CallableKind { get; }
    public IReadOnlyList<ParameterSymbol> Parameters { get; }
    public TypeRef? ReturnType { get; }

    /// <summary>
    /// For predicates, the narrowing type (e.g., `predicate isCall(Statement) : Call`).
    /// </summary>
    public TypeRef? NarrowingType { get; init; }

    /// <summary>
    /// Reference to the AST declaration (null for external/intrinsic functions).
    /// </summary>
    public FunctionDecl? Declaration { get; init; }

    public FunctionSymbol(string name, CallableKind callableKind,
        IReadOnlyList<ParameterSymbol> parameters, TypeRef? returnType = null)
        : base(name, SymbolKind.Function)
    {
        CallableKind = callableKind;
        Parameters = parameters;
        ReturnType = returnType;
    }
}

/// <summary>
/// A named type (record-like structure).
/// </summary>
public sealed class TypeSymbol : Symbol
{
    public string? BaseTypeName { get; }
    public IReadOnlyList<PropertySymbol> Properties { get; }

    public TypeSymbol(string name, string? baseTypeName, IReadOnlyList<PropertySymbol> properties)
        : base(name, SymbolKind.Type)
    {
        BaseTypeName = baseTypeName;
        Properties = properties;
    }
}

/// <summary>
/// A property within a type declaration.
/// </summary>
public sealed class PropertySymbol
{
    public string Name { get; }
    public TypeRef? DeclaredType { get; }
    public bool IsOptional { get; }

    public PropertySymbol(string name, TypeRef? declaredType, bool isOptional)
    {
        Name = name;
        DeclaredType = declaredType;
        IsOptional = isOptional;
    }
}

/// <summary>
/// An enum type.
/// </summary>
public sealed class EnumSymbol : Symbol
{
    public TypeRef? MemberType { get; }
    public IReadOnlyList<EnumMemberSymbol> Members { get; }

    public EnumSymbol(string name, TypeRef? memberType, IReadOnlyList<EnumMemberSymbol> members)
        : base(name, SymbolKind.Enum)
    {
        MemberType = memberType;
        Members = members;
    }
}

/// <summary>
/// A single enum member value.
/// </summary>
public sealed class EnumMemberSymbol : Symbol
{
    public string OwningEnumName { get; }

    public EnumMemberSymbol(string name, string owningEnumName)
        : base(name, SymbolKind.EnumMember)
    {
        OwningEnumName = owningEnumName;
    }
}

/// <summary>
/// An imported module.
/// </summary>
public sealed class ModuleSymbol : Symbol
{
    public IReadOnlyDictionary<string, Symbol> Exports { get; }

    public ModuleSymbol(string name, IReadOnlyDictionary<string, Symbol> exports)
        : base(name, SymbolKind.Module)
    {
        Exports = exports;
    }
}
