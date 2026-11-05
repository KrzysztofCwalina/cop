// A local or top-level variable introduced by `let`.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

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
