// A foreign/external function provided by the runtime.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

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
        {
            return ExtendedImpl(args, evaluator, env);
        }

        return Implementation(args, env);
    }

    public override string Display() => $"<extern {Name}>";
    public override string ToString() => Display();
}
