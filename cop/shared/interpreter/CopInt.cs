// An integer value used by Cop expression evaluation.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// An integer value used by Cop expression evaluation.
/// </summary>
public sealed class CopInt : CopValue
{
    /// <summary>
    /// The 32-bit integer represented by this language value.
    /// </summary>
    public int Value { get; }
    public CopInt(int value) => Value = value;
    /// <inheritdoc/>
    public override string Display() => Value.ToString();
    /// <inheritdoc/>
    public override string ToString() => Display();
}
