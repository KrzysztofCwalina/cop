// An enum type constructor.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// An enum type constructor. Calling TypeKind('value') produces a CopString
/// that can be compared to enum-typed properties. This provides explicit
/// string-to-enum conversion for extensible enum values.
/// </summary>
public sealed class CopEnumConstructor : CopValue, ICopCallable
{
    /// <summary>
    /// The enum type assigned to values constructed by this callable.
    /// </summary>
    public string EnumName { get; }
    /// <summary>
    /// The number of arguments still required to invoke this callable.
    /// </summary>
    public int Arity => 1;

    public CopEnumConstructor(string enumName)
    {
        EnumName = enumName;
    }

    /// <inheritdoc cref="ICopCallable.InvokeAsync"/>
    public async ValueTask<CopValue> InvokeAsync(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env, CancellationToken cancellationToken = default)
    {
        if (args.Count != 1)
        {
            throw new CopEvaluationException(
                $"Enum constructor '{EnumName}' expects exactly 1 argument, got {args.Count}", 0, null);
        }

        string arg = args[0] is CopString s ? s.Value : (await args[0].DisplayAsync(cancellationToken: cancellationToken));
        return new CopString(arg);
    }

    /// <inheritdoc/>
    public override string Display() => $"<enum {EnumName}>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
