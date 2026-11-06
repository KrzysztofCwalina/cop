// A string value with optional rich-text annotations.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A string value with optional rich-text annotations.
/// </summary>
public sealed class CopString : CopValue
{
    /// <summary>
    /// The CLR string represented by this language value.
    /// </summary>
    public string Value { get; }
    public CopString(string value) => Value = value;
    /// <inheritdoc/>
    public override string Display() => Value;
    /// <inheritdoc/>
    public override string ToString() => Value;
}
