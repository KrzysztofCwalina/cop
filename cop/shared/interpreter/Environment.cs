namespace Cop.Lang.Interpreter;

/// <summary>
/// A runtime environment (lexical scope chain) holding variable bindings.
/// Each environment has an optional parent, forming a chain for nested scopes.
/// </summary>
public sealed class Environment
{
    private readonly Dictionary<string, CopValue> _bindings = new(StringComparer.Ordinal);

    /// <summary>
    /// Enclosing environment (null for the global environment).
    /// </summary>
    public Environment? Parent { get; }

    public Environment(Environment? parent = null)
    {
        Parent = parent;
    }

    /// <summary>
    /// Define a binding in this environment.
    /// </summary>
    public void Define(string name, CopValue value)
    {
        _bindings[name] = value;
    }

    /// <summary>
    /// Look up a binding by name, walking the parent chain.
    /// Returns CopNull.Instance if not found.
    /// </summary>
    public CopValue Lookup(string name)
    {
        if (_bindings.TryGetValue(name, out var value))
            return value;
        if (Parent is not null)
            return Parent.Lookup(name);
        return CopNull.Instance;
    }

    /// <summary>
    /// Look up a binding, returning false if not found anywhere in the chain.
    /// </summary>
    public bool TryLookup(string name, out CopValue value)
    {
        if (_bindings.TryGetValue(name, out value!))
            return true;
        if (Parent is not null)
            return Parent.TryLookup(name, out value);
        value = CopNull.Instance;
        return false;
    }

    /// <summary>
    /// Create a child environment nested under this one.
    /// </summary>
    public Environment Extend()
    {
        return new Environment(this);
    }

    /// <summary>
    /// Returns all bindings in this environment (not walking parent chain).
    /// </summary>
    public IEnumerable<KeyValuePair<string, CopValue>> AllBindings() => _bindings;
}
