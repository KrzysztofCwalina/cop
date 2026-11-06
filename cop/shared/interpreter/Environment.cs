// Stores runtime bindings with parent environments for lexical lookup.

namespace Cop.Lang.Interpreter;

/// <summary>
/// A runtime environment (lexical scope chain) holding variable bindings.
/// Each environment has an optional parent, forming a chain for nested scopes.
/// </summary>
public sealed class Environment
{
    private Dictionary<string, CopValue>? _bindings;
    private string? _singleName;
    private CopValue _singleValue = CopNull.Instance;

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
        ArgumentNullException.ThrowIfNull(name);
        if (_bindings is not null)
        {
            _bindings[name] = value;
        }
        else if (_singleName is null || _singleName == name)
        {
            _singleName = name;
            _singleValue = value;
        }
        else
        {
            _bindings = new(StringComparer.Ordinal)
            {
                [_singleName] = _singleValue,
                [name] = value
            };
            _singleName = null;
            _singleValue = CopNull.Instance;
        }
    }

    /// <summary>
    /// Look up a binding by name, walking the parent chain.
    /// Returns CopNull.Instance if not found.
    /// </summary>
    public CopValue Lookup(string name) =>
        TryLookup(name, out CopValue value) ? value : CopNull.Instance;

    /// <summary>
    /// Look up a binding, returning false if not found anywhere in the chain.
    /// </summary>
    public bool TryLookup(string name, out CopValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_bindings is not null && _bindings.TryGetValue(name, out value!))
        {
            return true;
        }
        if (_singleName == name)
        {
            value = _singleValue;
            return true;
        }

        if (Parent is not null)
        {
            return Parent.TryLookup(name, out value);
        }

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
    public IEnumerable<KeyValuePair<string, CopValue>> AllBindings()
    {
        if (_bindings is not null)
        {
            foreach (KeyValuePair<string, CopValue> binding in _bindings)
            {
                yield return binding;
            }
        }
        else if (_singleName is not null)
        {
            yield return new(_singleName, _singleValue);
        }
    }
}
