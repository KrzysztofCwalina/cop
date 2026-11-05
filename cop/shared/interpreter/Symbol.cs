// A named entity in the program: variable, function, type, etc.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

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
