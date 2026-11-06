// A user-defined function or predicate with its closure environment.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A user-defined function or predicate with its closure environment.
/// </summary>
public sealed class CopFunction : CopValue, ICopCallable
{
    /// <summary>
    /// The parsed declaration that supplies the function's parameters and body.
    /// </summary>
    public Ast.FunctionDecl Declaration { get; }
    /// <summary>
    /// The lexical environment captured when the function was declared.
    /// </summary>
    public Environment Closure { get; }

    public CopFunction(Ast.FunctionDecl declaration, Environment closure)
    {
        Declaration = declaration;
        Closure = closure;
    }

    /// <summary>
    /// The number of arguments still required to invoke this callable.
    /// </summary>
    public int Arity => Declaration.Params.Count;

    /// <inheritdoc cref="ICopCallable.InvokeAsync"/>
    public async ValueTask<CopValue> InvokeAsync(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env, CancellationToken cancellationToken = default)
    {
        return (await evaluator.CallUserFunctionAsync(this, args, cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public override string Display() => $"<function {Declaration.Name}>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
