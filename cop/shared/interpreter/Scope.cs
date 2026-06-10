namespace Cop.Lang.Interpreter;

/// <summary>
/// A lexical scope containing symbol declarations.
/// Scopes form a chain via Parent pointers, enabling nested lookup.
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.Ordinal);

    /// <summary>
    /// Enclosing scope (null for the global/root scope).
    /// </summary>
    public Scope? Parent { get; }

    /// <summary>
    /// Descriptive label for debugging (e.g., "global", "function:myFunc", "foreach").
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// All symbols declared directly in this scope.
    /// </summary>
    public IReadOnlyDictionary<string, Symbol> Symbols => _symbols;

    public Scope(Scope? parent = null, string label = "")
    {
        Parent = parent;
        Label = label;
    }

    /// <summary>
    /// Declare a symbol in this scope.
    /// Returns false if a symbol with the same name already exists in this scope.
    /// </summary>
    public bool Declare(Symbol symbol)
    {
        return _symbols.TryAdd(symbol.Name, symbol);
    }

    /// <summary>
    /// Declare a symbol, replacing any existing symbol with the same name.
    /// Used for let-shadowing of imported functions.
    /// </summary>
    public void DeclareOrReplace(Symbol symbol)
    {
        _symbols[symbol.Name] = symbol;
    }

    /// <summary>
    /// Look up a symbol by name, walking the parent chain.
    /// Returns null if the name is not found in any enclosing scope.
    /// </summary>
    public Symbol? Resolve(string name)
    {
        if (_symbols.TryGetValue(name, out var symbol))
            return symbol;
        return Parent?.Resolve(name);
    }

    /// <summary>
    /// Look up a symbol by name in this scope only (no parent walk).
    /// </summary>
    public Symbol? ResolveLocal(string name)
    {
        _symbols.TryGetValue(name, out var symbol);
        return symbol;
    }

    /// <summary>
    /// Create a child scope nested under this one.
    /// </summary>
    public Scope CreateChild(string label = "")
    {
        return new Scope(this, label);
    }
}
