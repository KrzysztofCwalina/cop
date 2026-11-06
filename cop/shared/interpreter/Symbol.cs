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
    /// <summary>
    /// The identifier used to bind this declaration in a lexical scope.
    /// </summary>
    public string Name { get; }
    /// <summary>
    /// The category of declaration represented by this symbol.
    /// </summary>
    public SymbolKind Kind { get; }
    /// <summary>
    /// Whether importers of the declaring module can access this symbol.
    /// </summary>
    public bool IsExported { get; init; }
    /// <summary>
    /// The source line containing the declaration, when available.
    /// </summary>
    public int DeclarationLine { get; init; }

    protected Symbol(string name, SymbolKind kind)
    {
        Name = name;
        Kind = kind;
    }
}
