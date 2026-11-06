// The singleton null value used by the interpreter.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// The singleton null value used by the interpreter.
/// </summary>
// ============================================================================
// Primitive Values
// ============================================================================

public sealed class CopNull : CopValue
{
    public static readonly CopNull Instance = new();
    private CopNull() { }
    /// <inheritdoc/>
    public override bool IsTruthy => false;
    /// <inheritdoc/>
    public override string Display() => "null";
    /// <inheritdoc/>
    public override string ToString() => "null";
}
