// A floating-point value used by Cop expression evaluation.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A floating-point value used by Cop expression evaluation.
/// </summary>
public sealed class CopNumber : CopValue
{
    public double Value { get; }
    public CopNumber(double value) => Value = value;
    public override string Display() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override string ToString() => Display();
}
