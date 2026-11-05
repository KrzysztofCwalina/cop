// An interpreter boolean value backed by shared true and false instances.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// An interpreter boolean value backed by shared true and false instances.
/// </summary>
public sealed class CopBool : CopValue
{
    public static readonly CopBool True = new(true);
    public static readonly CopBool False = new(false);

    public bool Value { get; }
    private CopBool(bool value) => Value = value;
    public static CopBool Of(bool value) => value ? True : False;
    public override bool IsTruthy => Value;
    public override string Display() => Value ? "true" : "false";
    public override string ToString() => Display();
}
