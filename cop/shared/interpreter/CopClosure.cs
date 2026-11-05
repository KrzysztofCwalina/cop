// A partially-applied function (currying).

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A partially-applied function (currying). Holds a target function and the explicit arguments
/// bound so far, EXCLUDING the implicit item parameter (the leading type-named parameter that a
/// filter fills per element). Direct calls accumulate more explicit arguments (see EvalCall);
/// when invoked as a callable (e.g. by a filter) the first argument is taken as the item and the
/// bound explicit arguments complete the call. Enables the documented currying form:
/// `let bracketed = format('[')` then `Types:bracketed(']')`.
/// </summary>
public sealed class CopClosure : CopValue, ICopCallable
{
    public CopFunction Func { get; }
    public IReadOnlyList<CopValue> BoundExplicit { get; }
    /// <summary>Number of explicit parameters (declared params minus the leading item parameter).</summary>
    public int ExplicitArity { get; }

    public CopClosure(CopFunction func, IReadOnlyList<CopValue> boundExplicit, int explicitArity)
    {
        Func = func;
        BoundExplicit = boundExplicit;
        ExplicitArity = explicitArity;
    }

    /// <summary>Returns a new closure with additional explicit arguments bound (direct application).</summary>
    public CopClosure WithExplicit(IEnumerable<CopValue> more)
        => new(Func, [.. BoundExplicit, .. more], ExplicitArity);

    public int Arity => ExplicitArity - BoundExplicit.Count;

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        // Invoked as a callable (e.g. by a filter): the first argument is the item that fills the
        // leading parameter; any further arguments complete the remaining explicit parameters
        // together with the already-bound ones.
        if (args.Count == 0)
        {
            return this;
        }

        List<CopValue> full = new List<CopValue> { args[0] };
        full.AddRange(BoundExplicit);
        for (int i = 1; i < args.Count; i++)
        {
            full.Add(args[i]);
        }

        return evaluator.CallUserFunction(Func, full);
    }

    public override string Display() => $"<closure {Func.Declaration.Name}>";
    public override string ToString() => Display();
}
