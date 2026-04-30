namespace Cop.Core;

/// <summary>
/// Describes a query against a collection source.
/// Carries the collection name, optional document context, and pushdown filter.
/// </summary>
public class CollectionQuery
{
    /// <summary>Name of the collection being queried (e.g., "Types", "DiskFiles").</summary>
    public required string CollectionName { get; init; }

    /// <summary>
    /// For per-document sources (e.g., code extractors), the document to extract from.
    /// Null for global sources (e.g., filesystem scan results).
    /// </summary>
    public object? Document { get; init; }

    /// <summary>
    /// Pushdown filter expression. The source can use this to avoid materializing
    /// items that will be filtered out. Sources that don't support pushdown
    /// return all items — the engine applies filters locally as fallback.
    /// </summary>
    public FilterExpression? Filter { get; init; }

    /// <summary>Root path of the project, for sources that need filesystem access.</summary>
    public string? RootPath { get; init; }
}

/// <summary>
/// A queryable data source for a collection. Replaces eager collection registration
/// with a lazy/queryable abstraction that supports filter pushdown.
/// </summary>
public interface ICollectionSource
{
    /// <summary>
    /// Queries the source for items, optionally applying pushdown filters.
    /// Returns all items if no filter is provided or if the source doesn't support pushdown.
    /// </summary>
    List<object> Query(CollectionQuery query);

    /// <summary>
    /// Whether this source supports per-document extraction (true) or is global (false).
    /// </summary>
    bool IsPerDocument { get; }
}

/// <summary>
/// A streaming (potentially infinite) data source. Used by push-like providers
/// such as HTTP servers that yield items indefinitely.
/// </summary>
public interface IStreamingCollectionSource
{
    /// <summary>
    /// Returns an async enumerable of items. The stream may be infinite (e.g., HTTP requests).
    /// </summary>
    IAsyncEnumerable<object> QueryStream(CancellationToken cancellationToken = default);

    /// <summary>
    /// The name of the collection (e.g., "Receive").
    /// </summary>
    string CollectionName { get; }
}
