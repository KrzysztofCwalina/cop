// Collection source backed by a pre-computed global list.

using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Collection source backed by a pre-computed global list.
/// Used by filesystem provider (Files, Folders) and external providers.
/// Supports filter pushdown using registered property accessors.
/// </summary>
public class GlobalCollectionSource : ICollectionSource
{
    private readonly List<object> _items;
    private readonly Func<FilterExpression, Func<object, bool>>? _filterCompiler;

    /// <inheritdoc/>
    public override bool IsPerDocument => false;

    public GlobalCollectionSource(
        List<object> items,
        Func<FilterExpression, Func<object, bool>>? filterCompiler = null)
    {
        _items = items;
        _filterCompiler = filterCompiler;
    }

    /// <inheritdoc/>
    public override List<object> Query(CollectionQuery query)
    {
        if (query.Filter is not null && _filterCompiler is not null)
        {
            Func<object, bool> predicate = _filterCompiler(query.Filter);
            return _items.Where(predicate).ToList();
        }

        return _items;
    }
}
