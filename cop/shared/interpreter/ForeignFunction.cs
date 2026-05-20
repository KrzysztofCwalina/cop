namespace Cop.Lang.Interpreter;

/// <summary>
/// Delegate type for foreign (external) functions.
/// Foreign functions receive evaluated arguments and the current environment.
/// They must ALWAYS return a CopValue (use CopNull.Instance for void).
/// </summary>
public delegate CopValue ForeignFunction(IReadOnlyList<CopValue> args, Environment env);

/// <summary>
/// Registry for foreign functions provided by the runtime.
/// This is the sole extension point for adding built-in behavior.
/// </summary>
public sealed class ForeignFunctionRegistry
{
    private readonly Dictionary<string, CopExternalFunction> _functions = new(StringComparer.Ordinal);

    /// <summary>
    /// Register a foreign function by name.
    /// </summary>
    public void Register(string name, ForeignFunction impl, int arity = -1)
    {
        _functions[name] = new CopExternalFunction(name, impl, arity);
    }

    /// <summary>
    /// Resolve a foreign function by name.
    /// Returns null if no function is registered with that name.
    /// </summary>
    public CopExternalFunction? Resolve(string name)
    {
        _functions.TryGetValue(name, out var fn);
        return fn;
    }

    /// <summary>
    /// Get all registered function names.
    /// </summary>
    public IEnumerable<string> Names => _functions.Keys;

    /// <summary>
    /// Populate an environment with all registered foreign functions.
    /// </summary>
    public void PopulateEnvironment(Environment env)
    {
        foreach (var (name, fn) in _functions)
            env.Define(name, fn);
    }
}
