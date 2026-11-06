// Describes the collection, document context, and filter passed to a data source.

namespace Cop.Core;

/// <summary>
/// A collection-source query with optional document context and filter pushdown.
/// </summary>
public class CollectionQuery
{
    /// <summary>Name of the collection being queried, such as Types or Files.</summary>
    public required string CollectionName { get; init; }

    /// <summary>
    /// The document to extract from for per-document sources; null for global sources.
    /// </summary>
    public object? Document { get; init; }

    /// <summary>
    /// Optional pushdown filter. Sources without pushdown support return all items
    /// for the engine to filter locally.
    /// </summary>
    public FilterExpression? Filter { get; init; }

    /// <summary>
    /// Root path of the project for sources that access the filesystem.
    /// </summary>
    public string? RootPath { get; init; }
}
