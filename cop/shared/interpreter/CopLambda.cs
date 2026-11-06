// A callable lambda expression paired with its captured lexical environment.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A callable lambda expression paired with its captured lexical environment.
/// </summary>
public sealed class CopLambda : CopValue, ICopCallable
{
    /// <summary>
    /// The parsed lambda expression containing its parameters and body.
    /// </summary>
    public Ast.LambdaExpr Expr { get; }
    /// <summary>
    /// The lexical environment captured when the lambda was evaluated.
    /// </summary>
    public Environment Closure { get; }

    public CopLambda(Ast.LambdaExpr expr, Environment closure)
    {
        Expr = expr;
        Closure = closure;
    }

    /// <summary>
    /// The number of arguments still required to invoke this callable.
    /// </summary>
    public int Arity => Expr.Params.Count;

    /// <inheritdoc cref="ICopCallable.InvokeAsync"/>
    public async ValueTask<CopValue> InvokeAsync(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env, CancellationToken cancellationToken = default)
    {
        return (await evaluator.CallLambdaAsync(this, args, cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public override string Display() => "<lambda>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
