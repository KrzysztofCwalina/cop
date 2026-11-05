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
    public string EnumName { get; }
    public int Arity => 1;

    public CopEnumConstructor(string enumName)
    {
        EnumName = enumName;
    }

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        if (args.Count != 1)
        {
            throw new CopEvaluationException(
                $"Enum constructor '{EnumName}' expects exactly 1 argument, got {args.Count}", 0, null);
        }

        string arg = args[0] is CopString s ? s.Value : args[0].Display();
        return new CopString(arg);
    }

    public override string Display() => $"<enum {EnumName}>";
    public override string ToString() => Display();
}
