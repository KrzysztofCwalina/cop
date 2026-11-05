// A user-defined function or predicate with its closure environment.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

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
