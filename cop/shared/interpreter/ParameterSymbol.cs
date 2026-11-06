// A function parameter.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A function parameter.
/// </summary>
public sealed class ParameterSymbol : Symbol
{
    /// <summary>
    /// The parameter's source type annotation, when present.
    /// </summary>
    public TypeRef? DeclaredType { get; }
    /// <summary>
    /// The zero-based position of the parameter in its declaration.
    /// </summary>
    public int Ordinal { get; }

    public ParameterSymbol(string name, TypeRef? declaredType, int ordinal)
        : base(name, SymbolKind.Parameter)
    {
        DeclaredType = declaredType;
        Ordinal = ordinal;
    }
}
