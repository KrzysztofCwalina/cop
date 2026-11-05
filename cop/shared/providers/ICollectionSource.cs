// Defines enumeration and query capabilities exposed by provider collections.

namespace Cop.Core;

/// <summary>
/// A queryable data source for a collection. Replaces eager collection registration
/// with a lazy/queryable abstraction that supports filter pushdown.
/// </summary>
public abstract class ICollectionSource
{
    /// <summary>
    /// Queries the source for items, optionally applying pushdown filters.
    /// Returns all items if no filter is provided or if the source doesn't support pushdown.
    /// </summary>
    public abstract List<object> Query(CollectionQuery query);

    /// <summary>
    /// Whether this source supports per-document extraction (true) or is global (false).
    /// </summary>
    public abstract bool IsPerDocument { get; }
}
