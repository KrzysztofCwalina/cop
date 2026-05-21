namespace Cop.Lang.Interpreter;

/// <summary>
/// Base class for all runtime values in the Cop language.
/// The evaluator ALWAYS returns a CopValue (never C# null).
/// Use CopNull.Instance for the language's null value.
/// </summary>
public abstract class CopValue
{
    /// <summary>
    /// Truthiness: all values are truthy except CopNull and CopBool(false).
    /// </summary>
    public virtual bool IsTruthy => true;

    /// <summary>
    /// Display representation for interpolation / print.
    /// </summary>
    public abstract string Display();
}

// ============================================================================
// Primitive Values
// ============================================================================

public sealed class CopNull : CopValue
{
    public static readonly CopNull Instance = new();
    private CopNull() { }
    public override bool IsTruthy => false;
    public override string Display() => "null";
    public override string ToString() => "null";
}

public sealed class CopBool : CopValue
{
    public static readonly CopBool True = new(true);
    public static readonly CopBool False = new(false);

    public bool Value { get; }
    private CopBool(bool value) => Value = value;
    public static CopBool Of(bool value) => value ? True : False;
    public override bool IsTruthy => Value;
    public override string Display() => Value ? "true" : "false";
    public override string ToString() => Display();
}

public sealed class CopInt : CopValue
{
    public int Value { get; }
    public CopInt(int value) => Value = value;
    public override string Display() => Value.ToString();
    public override string ToString() => Display();
}

public sealed class CopNumber : CopValue
{
    public double Value { get; }
    public CopNumber(double value) => Value = value;
    public override string Display() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override string ToString() => Display();
}

public sealed class CopString : CopValue
{
    public string Value { get; }
    public CopString(string value) => Value = value;
    public override string Display() => Value;
    public override string ToString() => Value;
}

// ============================================================================
// Composite Values
// ============================================================================

/// <summary>
/// Eager, finite list of values.
/// </summary>
public sealed class CopList : CopValue
{
    public IReadOnlyList<CopValue> Items { get; }
    public CopList(IReadOnlyList<CopValue> items) => Items = items;
    public override string Display() => $"[{string.Join(", ", Items.Select(i => i.Display()))}]";
    public override string ToString() => Display();
}

/// <summary>
/// Lazy collection — items are produced on demand.
/// Uses a factory function so the collection can be re-enumerated.
/// </summary>
public sealed class CopLazyCollection : CopValue
{
    private readonly Func<IEnumerable<CopValue>> _factory;
    public CopLazyCollection(Func<IEnumerable<CopValue>> factory) => _factory = factory;
    public IEnumerable<CopValue> Enumerate() => _factory();
    public override string Display() => "[...]";
    public override string ToString() => Display();
}

/// <summary>
/// A deferred computation (thunk). Self-forcing: accessing Display() or IsTruthy
/// transparently forces evaluation. Memoized: the computation runs at most once.
/// Cycle detection: forcing a thunk that is already being forced throws.
/// </summary>
public sealed class CopThunk : CopValue
{
    private readonly Func<CopValue> _compute;
    private CopValue? _forced;
    private bool _forcing;

    public CopThunk(Func<CopValue> compute) => _compute = compute;

    /// <summary>
    /// Force evaluation of this thunk. Returns the memoized result.
    /// Recursively forces if the result is itself a thunk.
    /// </summary>
    public CopValue Force()
    {
        if (_forced is not null) return _forced;
        if (_forcing) throw new CopEvaluationException("Recursive thunk forcing detected (infinite loop)");
        _forcing = true;
        try
        {
            var result = _compute();
            // Recursively force nested thunks
            while (result is CopThunk nested)
                result = nested.Force();
            _forced = result;
            return _forced;
        }
        finally { _forcing = false; }
    }

    /// <summary>True if this thunk has already been forced to a concrete value.</summary>
    public bool IsForced => _forced is not null;

    public override bool IsTruthy => Force().IsTruthy;
    public override string Display() => Force().Display();
    public override string ToString() => Force().ToString() ?? Display();
}

/// <summary>
/// Language-created object literal: { Name = 'foo', Age = 42 }
/// </summary>
public sealed class CopObject : CopValue
{
    public IReadOnlyDictionary<string, CopValue> Fields { get; }
    public string? TypeName { get; init; }
    public CopObject(IReadOnlyDictionary<string, CopValue> fields) => Fields = fields;

    public bool HasField(string name) => Fields.ContainsKey(name);

    public CopValue GetField(string name) =>
        Fields.TryGetValue(name, out var val) ? val
        : name == "Type" && TypeName is not null ? new CopString(TypeName)
        : CopNull.Instance;

    public override string Display()
    {
        var pairs = Fields.Select(kv => $"{kv.Key} = {kv.Value.Display()}");
        return $"{{ {string.Join(", ", pairs)} }}";
    }
    public override string ToString() => Display();
}

/// <summary>
/// Wraps a provider-backed dynamic object. Fields are resolved lazily.
/// </summary>
public sealed class CopDynamicObject : CopValue
{
    private readonly IDynamicObjectAdapter _adapter;
    private readonly object _underlying;

    public CopDynamicObject(object underlying, IDynamicObjectAdapter adapter)
    {
        _underlying = underlying;
        _adapter = adapter;
    }

    public object Underlying => _underlying;
    public string? TypeName => _adapter.TypeName;

    public bool HasField(string name)
    {
        var val = _adapter.GetField(_underlying, name);
        if (val is not CopNull) return true;
        return name == "Type" && _adapter.TypeName is not null;
    }

    public CopValue GetField(string name)
    {
        var val = _adapter.GetField(_underlying, name);
        if (val is CopNull && name == "Type" && _adapter.TypeName is not null)
            return new CopString(_adapter.TypeName);
        return val;
    }

    public override string Display() => _adapter.Display(_underlying);
    public override string ToString() => Display();
}

/// <summary>
/// Interface for adapting provider objects (DataObject, etc.) to the evaluator.
/// Implemented by the runtime bridge — keeps provider knowledge out of core.
/// </summary>
public interface IDynamicObjectAdapter
{
    CopValue GetField(object obj, string name);
    string Display(object obj);
    string? TypeName => null;
}

// ============================================================================
// Callable Values
// ============================================================================

/// <summary>
/// Common interface for anything that can be called.
/// </summary>
public interface ICopCallable
{
    CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env);
    int Arity { get; }
}

/// <summary>
/// A user-defined function or predicate with its closure environment.
/// </summary>
public sealed class CopFunction : CopValue, ICopCallable
{
    public Ast.FunctionDecl Declaration { get; }
    public Environment Closure { get; }

    public CopFunction(Ast.FunctionDecl declaration, Environment closure)
    {
        Declaration = declaration;
        Closure = closure;
    }

    public int Arity => Declaration.Params.Count;

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        return evaluator.CallUserFunction(this, args);
    }

    public override string Display() => $"<function {Declaration.Name}>";
    public override string ToString() => Display();
}

/// <summary>
/// A group of overloaded functions with the same name but different typed parameters.
/// Dispatches to the correct overload based on the first argument's type.
/// </summary>
public sealed class CopFunctionGroup : CopValue, ICopCallable
{
    public string Name { get; }
    private readonly List<CopFunction> _overloads = [];

    public CopFunctionGroup(string name)
    {
        Name = name;
    }

    public void Add(CopFunction func) => _overloads.Add(func);

    public int Arity => _overloads.Count > 0 ? _overloads[0].Arity : 0;

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        // Try to find matching overload by first parameter type
        if (args.Count > 0)
        {
            var subject = args[0];
            var subjectTypeName = GetTypeName(subject);

            if (subjectTypeName is not null)
            {
                foreach (var overload in _overloads)
                {
                    if (overload.Declaration.Params.Count > 0)
                    {
                        var firstParam = overload.Declaration.Params[0];
                        var paramType = firstParam.Type?.Name;
                        if (paramType is not null &&
                            string.Equals(paramType, subjectTypeName, StringComparison.OrdinalIgnoreCase))
                        {
                            return evaluator.CallUserFunction(overload, args);
                        }
                    }
                }
            }

            // Try matching by arity
            foreach (var overload in _overloads)
            {
                if (overload.Arity == args.Count)
                    return evaluator.CallUserFunction(overload, args);
            }
        }

        // Fall back to last registered overload
        return evaluator.CallUserFunction(_overloads[^1], args);
    }

    private static string? GetTypeName(CopValue value) => value switch
    {
        CopThunk thunk => GetTypeName(thunk.Force()),
        CopDynamicObject dyn => dyn.TypeName,
        CopObject obj => obj.TypeName,
        CopString => "string",
        CopInt => "int",
        CopNumber => "number",
        CopBool => "bool",
        CopList => "collection",
        CopLazyCollection => "collection",
        _ => null
    };

    public override string Display() => $"<function-group {Name} ({_overloads.Count} overloads)>";
    public override string ToString() => Display();
}
public sealed class CopLambda : CopValue, ICopCallable
{
    public Ast.LambdaExpr Expr { get; }
    public Environment Closure { get; }

    public CopLambda(Ast.LambdaExpr expr, Environment closure)
    {
        Expr = expr;
        Closure = closure;
    }

    public int Arity => Expr.Params.Count;

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        return evaluator.CallLambda(this, args);
    }

    public override string Display() => "<lambda>";
    public override string ToString() => Display();
}

/// <summary>
/// A foreign/external function provided by the runtime.
/// </summary>
public sealed class CopExternalFunction : CopValue, ICopCallable
{
    public string Name { get; }
    public ForeignFunction Implementation { get; }
    public ForeignFunctionEx? ExtendedImpl { get; init; }
    public int Arity { get; }

    public CopExternalFunction(string name, ForeignFunction impl, int arity = -1)
    {
        Name = name;
        Implementation = impl;
        Arity = arity;
    }

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        if (ExtendedImpl is not null)
            return ExtendedImpl(args, evaluator, env);
        return Implementation(args, env);
    }

    public override string Display() => $"<extern {Name}>";
    public override string ToString() => Display();
}

/// <summary>
/// A provider proxy returned by `object('providerName')`.
/// Member access resolves to provider collections registered in the environment.
/// For example: object('code').Types → looks up "Types" or "code.Types" in the environment.
/// </summary>
public sealed class CopProviderProxy : CopValue
{
    public string ProviderName { get; }
    private readonly Environment _env;

    public CopProviderProxy(string providerName, Environment env)
    {
        ProviderName = providerName;
        _env = env;
    }

    public bool HasField(string name)
    {
        // Check qualified name first (provider.Collection), then bare name
        if (_env.TryLookup($"{ProviderName}.{name}", out _)) return true;
        if (_env.TryLookup(name, out _)) return true;
        return false;
    }

    public CopValue GetField(string name)
    {
        // Try qualified name first (e.g., "code.Types")
        if (_env.TryLookup($"{ProviderName}.{name}", out var qualified))
        {
            // Skip empty placeholder collections — fall through to bare name
            if (qualified is not CopList { Items.Count: 0 })
                return qualified;
        }
        // Then try bare name (e.g., "Types")
        if (_env.TryLookup(name, out var bare))
            return bare;
        // Return qualified even if empty (rather than null) if bare also not found
        return qualified ?? CopNull.Instance;
    }

    public override string Display() => $"<provider {ProviderName}>";
    public override string ToString() => Display();
}
