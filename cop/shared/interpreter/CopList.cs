// Eager, finite list of values.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

// ============================================================================
// Composite Values
// ============================================================================

/// <summary>
/// Eager, finite list of values.
/// </summary>
public sealed class CopList : CopValue
{
    internal QuerySource? Source { get; set; }
    public IReadOnlyList<CopValue> Items { get; }
    public CopList(IReadOnlyList<CopValue> items) => Items = items;
    public override string Display() => $"[{string.Join(", ", Items.Select(i => i.Display()))}]";
    public override string ToString() => Display();
}
