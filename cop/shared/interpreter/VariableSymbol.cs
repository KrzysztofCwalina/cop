// A local or top-level variable introduced by `let`.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A local or top-level variable introduced by `let`.
/// </summary>
public sealed class VariableSymbol : Symbol
{
    /// <summary>
    /// The binding's source type annotation, when present.
    /// </summary>
    public TypeRef? DeclaredType { get; }
    /// <summary>
    /// Whether the binding may be assigned after its initial definition.
    /// </summary>
    public bool IsReadOnly { get; }

    public VariableSymbol(string name, TypeRef? declaredType = null, bool isReadOnly = true)
        : base(name, SymbolKind.Variable)
    {
        DeclaredType = declaredType;
        IsReadOnly = isReadOnly;
    }
}
