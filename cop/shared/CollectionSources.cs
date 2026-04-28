using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Collection source backed by a per-document extractor function.
/// Used by per-document providers (Types, Statements, etc.) where each document produces items.
/// Supports filter pushdown using registered property accessors.
/// </summary>
public class DocumentCollectionSource : ICollectionSource
{
    private readonly Func<Document, List<object>> _extractor;
    private readonly Func<FilterExpression, Func<object, bool>>? _filterCompiler;

    public bool IsPerDocument => true;

    public DocumentCollectionSource(
        Func<Document, List<object>> extractor,
        Func<FilterExpression, Func<object, bool>>? filterCompiler = null)
    {
        _extractor = extractor;
        _filterCompiler = filterCompiler;
    }

    public List<object> Query(CollectionQuery query)
    {
        if (query.Document is not Document doc)
            return [];

        var items = _extractor(doc);

        // Apply pushdown filter if compiler is available
        if (query.Filter is not null && _filterCompiler is not null)
        {
            var predicate = _filterCompiler(query.Filter);
            return items.Where(predicate).ToList();
        }

        return items;
    }
}

/// <summary>
/// Collection source backed by a pre-computed global list.
/// Used by filesystem provider (DiskFiles, Folders) and external providers.
/// Supports filter pushdown using registered property accessors.
/// </summary>
public class GlobalCollectionSource : ICollectionSource
{
    private readonly List<object> _items;
    private readonly Func<FilterExpression, Func<object, bool>>? _filterCompiler;

    public bool IsPerDocument => false;

    public GlobalCollectionSource(
        List<object> items,
        Func<FilterExpression, Func<object, bool>>? filterCompiler = null)
    {
        _items = items;
        _filterCompiler = filterCompiler;
    }

    public List<object> Query(CollectionQuery query)
    {
        if (query.Filter is not null && _filterCompiler is not null)
        {
            var predicate = _filterCompiler(query.Filter);
            return _items.Where(predicate).ToList();
        }

        return _items;
    }
}
