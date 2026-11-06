// A foreign/external function provided by the runtime.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A foreign/external function provided by the runtime.
/// </summary>
public sealed class CopExternalFunction : CopValue, ICopCallable
{
    /// <summary>
    /// The name registered for this foreign function.
    /// </summary>
    public string Name { get; }
    /// <summary>
    /// The foreign function delegate used for ordinary invocation.
    /// </summary>
    public ForeignFunction Implementation { get; }
    /// <summary>
    /// An optional implementation that also receives the calling expression.
    /// </summary>
    public ForeignFunctionEx? ExtendedImpl { get; init; }
    /// <summary>
    /// The number of arguments still required to invoke this callable.
    /// </summary>
    public int Arity { get; }

    public CopExternalFunction(string name, ForeignFunction impl, int arity = -1)
    {
        Name = name;
        Implementation = impl;
        Arity = arity;
    }

    /// <summary>Wraps a genuinely synchronous foreign function as a completed operation.</summary>
    public CopExternalFunction(string name,
        Func<IReadOnlyList<CopValue>, Environment, CopValue> impl, int arity = -1)
        : this(name, (args, env, _) => ValueTask.FromResult(impl(args, env)), arity) { }

    /// <inheritdoc cref="ICopCallable.InvokeAsync"/>
    public ValueTask<CopValue> InvokeAsync(IReadOnlyList<CopValue> args, Evaluator evaluator,
        Environment env, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExtendedImpl is not null)
        {
            return ExtendedImpl(args, evaluator, env, cancellationToken);
        }

        return Implementation(args, env, cancellationToken);
    }

    /// <inheritdoc/>
    public override string Display() => $"<extern {Name}>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
