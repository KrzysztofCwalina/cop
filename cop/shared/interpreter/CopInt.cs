// An integer value used by Cop expression evaluation.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// An integer value used by Cop expression evaluation.
/// </summary>
public sealed class CopInt : CopValue
{
    public int Value { get; }
    public CopInt(int value) => Value = value;
    public override string Display() => Value.ToString();
    public override string ToString() => Display();
}
