// A function parameter.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

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
