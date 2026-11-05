// A string value with optional rich-text annotations.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A string value with optional rich-text annotations.
/// </summary>
public sealed class CopString : CopValue
{
    public string Value { get; }
    public CopString(string value) => Value = value;
    public override string Display() => Value;
    public override string ToString() => Value;
}
