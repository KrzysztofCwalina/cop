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
    /// <summary>
    /// The ordered values stored in this eager language collection.
    /// </summary>
    public IReadOnlyList<CopValue> Items { get; }
    public CopList(IReadOnlyList<CopValue> items) => Items = items;
    /// <inheritdoc/>
    public override string Display() => $"[{string.Join(", ", Items.Select(i => i.Display()))}]";
    /// <inheritdoc/>
    public override async ValueTask<string> DisplayAsync(
        CancellationToken cancellationToken = default)
    {
        List<string> items = new(Items.Count);
        foreach (CopValue item in Items)
        {
            items.Add(await item.DisplayAsync(cancellationToken).ConfigureAwait(false));
        }
        return $"[{string.Join(", ", items)}]";
    }
    /// <inheritdoc/>
    public override string ToString() => Display();
}
