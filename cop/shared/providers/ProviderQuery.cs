// Describes what the engine is requesting from a provider.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Describes what the engine is requesting from a provider.
/// </summary>
public class ProviderQuery
{
    public ProviderQuery ForCollection(string collection, FilterExpression? filter = null) => new()
    {
        RootPath = RootPath,
        Collection = collection,
        Filter = filter ?? Filter,
        CollectionFilters = CollectionFilters,
        ExcludedDirectories = ExcludedDirectories,
        Options = Options
    };
    /// <summary>
    /// Root path of the project, or null for non-file-backed providers.
    /// </summary>
    public string? RootPath { get; init; }

    /// <summary>
    /// Select a single collection by name. When set, the provider only needs
    /// to populate this collection in its response (optimization hint).
    /// Null means return all collections.
    /// </summary>
    public string? Collection { get; init; }

    /// <summary>
    /// Pushdown filter expression (combined). Providers that support query optimization
    /// can inspect this to avoid materializing items that will be filtered out.
    /// Providers that don't support pushdown can ignore this — the engine
    /// will apply filters locally as a fallback.
    /// </summary>
    public FilterExpression? Filter { get; init; }

    /// <summary>
    /// Per-collection pushdown filters. Keyed by collection name (e.g., "Types", "Projects").
    /// The engine uses this to apply collection-specific filters after items are returned.
    /// Providers may also inspect this for internal optimization.
    /// </summary>
    public IReadOnlyDictionary<string, FilterExpression>? CollectionFilters { get; init; }

    /// <summary>
    /// Directory names to skip during recursive filesystem scanning.
    /// The caller owns this policy (e.g., .git, node_modules, bin, obj).
    /// Providers that walk directories should prune these during traversal.
    /// </summary>
    public IReadOnlySet<string>? ExcludedDirectories { get; init; }

    /// <summary>
    /// Extensible options bag for future needs (e.g. query language parameters).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}
