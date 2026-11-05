// A deferred computation (thunk).

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A deferred computation (thunk). Self-forcing: accessing Display() or IsTruthy
/// transparently forces evaluation. Ordinary computations are memoized; templates
/// that read a supplied item are rendered separately for each item context.
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
    public CopValue Force() => _forced ?? ForceCore(() => (_compute(), true));

    internal CopValue ForceTemplate(Func<(CopValue Value, bool Cache)> render) => ForceCore(render);

    private CopValue ForceCore(Func<(CopValue Value, bool Cache)> compute)
    {
        if (_forced is not null)
        {
            return _forced;
        }

        if (_forcing)
        {
            throw new CopEvaluationException("Recursive thunk forcing detected (infinite loop)");
        }

        _forcing = true;
        try
        {
            (CopValue? result, bool cache) = compute();
            // Recursively force nested thunks
            while (result is CopThunk nested)
            {
                result = nested.Force();
            }

            if (cache)
            {
                _forced = result;
            }

            return result;
        }
        finally { _forcing = false; }
    }

    /// <summary>True if this thunk has already been forced to a concrete value.</summary>
    public bool IsForced => _forced is not null;
    internal CopValue? ForcedValue => _forced;
    internal Ast.Expression? ConstantExpression { get; init; }
    internal Environment? ConstantEnvironment { get; init; }
    internal Func<Environment, CopValue>? EvaluateInEnvironment { get; init; }

    public override bool IsTruthy => Force().IsTruthy;
    public override string Display() => Force().Display();
    public override string ToString() => Force().ToString() ?? Display();
}
