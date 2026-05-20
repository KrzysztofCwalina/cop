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
/// Language-created object literal: { Name = 'foo', Age = 42 }
/// </summary>
public sealed class CopObject : CopValue
{
    public IReadOnlyDictionary<string, CopValue> Fields { get; }
    public CopObject(IReadOnlyDictionary<string, CopValue> fields) => Fields = fields;

    public CopValue GetField(string name) =>
        Fields.TryGetValue(name, out var val) ? val : CopNull.Instance;

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

    public CopValue GetField(string name) => _adapter.GetField(_underlying, name);

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
/// A lambda (anonymous function) with its closure environment.
/// </summary>
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
    public int Arity { get; }

    public CopExternalFunction(string name, ForeignFunction impl, int arity = -1)
    {
        Name = name;
        Implementation = impl;
        Arity = arity;
    }

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        return Implementation(args, env);
    }

    public override string Display() => $"<extern {Name}>";
    public override string ToString() => Display();
}
