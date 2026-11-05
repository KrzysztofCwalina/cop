// A callable lambda expression paired with its captured lexical environment.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A callable lambda expression paired with its captured lexical environment.
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
