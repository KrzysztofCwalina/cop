using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;

namespace Cop.Providers;

/// <summary>
/// Service for querying providers with path overrides at evaluation time.
/// Caches results by (provider, collection, absolutePath) to avoid re-scanning.
/// Used by the interpreter when a collection reference has a PathOverride.
/// </summary>
public class ProviderQueryService
{
    private readonly record struct CacheKey(string ProviderName, string CollectionName, string AbsolutePath);

    private readonly Dictionary<CacheKey, List<object>> _cache = new();
    private readonly Dictionary<string, (DataProvider Instance, ProviderSchema Schema)> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _invocationDirectory;
    private readonly IReadOnlySet<string>? _excludedDirectories;
    private readonly List<string> _warnings = [];
    private Action<string>? _diagLog;

    public IReadOnlyList<string> Warnings => _warnings;

    public ProviderQueryService(string invocationDirectory, IReadOnlySet<string>? excludedDirectories = null, Action<string>? diagLog = null)
    {
        _invocationDirectory = invocationDirectory;
        _excludedDirectories = excludedDirectories;
        _diagLog = diagLog;
    }

    /// <summary>
    /// Registers a provider so it can be queried by name at evaluation time.
    /// </summary>
    public void RegisterProvider(string name, DataProvider instance, ProviderSchema schema)
    {
        _providers[name] = (instance, schema);
    }

    /// <summary>
    /// Queries a collection from a provider with a path override.
    /// The path is resolved relative to the invocation directory (process CWD).
    /// Results are cached by (provider, collection, absolutePath).
    /// </summary>
    /// <param name="providerName">Provider namespace (e.g., "csharp", "files")</param>
    /// <param name="collectionName">Collection name (e.g., "Types", "Files")</param>
    /// <param name="pathOverride">Path to scan (relative to invocation directory or absolute)</param>
    /// <returns>Collection items, or empty list if the path is invalid or provider fails.</returns>
    public List<object> Query(string providerName, string collectionName, string pathOverride)
    {
        // Resolve relative path against invocation directory (process CWD, not -t root)
        var absolutePath = Path.IsPathRooted(pathOverride)
            ? Path.GetFullPath(pathOverride)
            : Path.GetFullPath(Path.Combine(_invocationDirectory, pathOverride));

        var key = new CacheKey(providerName, collectionName, absolutePath);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        if (!_providers.TryGetValue(providerName, out var provider))
        {
            _warnings.Add($"Provider '{providerName}' not found for path-scoped query.");
            var empty = new List<object>();
            _cache[key] = empty;
            return empty;
        }

        if (!Directory.Exists(absolutePath))
        {
            throw new InvalidOperationException($"Directory '{pathOverride}' not found.");
        }

        var query = new ProviderQuery
        {
            RootPath = absolutePath,
            Collection = collectionName,
            ExcludedDirectories = _excludedDirectories
        };

        _diagLog?.Invoke($"[diag] Path-scoped query: {providerName}.{collectionName} RootPath={absolutePath}");

        try
        {
            var (instance, schema) = provider;
            var collections = ProviderLoader.QueryCollections(instance, schema, query);
            if (collections.TryGetValue(collectionName, out var items))
            {
                _cache[key] = items;
                return items;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _warnings.Add($"Error querying provider '{providerName}' for collection '{collectionName}' at path '{absolutePath}': {ex.Message}");
        }

        // Don't cache — the provider may not have returned this collection due to a transient issue.
        // The next call will retry the query.
        return new List<object>();
    }

    /// <summary>
    /// Queries a provider with a ProviderQuery and returns the result as a CopValue.
    /// The provider decides what collections to return. Single collection → CopList, multiple → CopObject.
    /// </summary>
    public CopValue QueryProvider(string providerName, ProviderQuery query)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            _warnings.Add($"Provider '{providerName}' not found.");
            return CopNull.Instance;
        }

        var (instance, schema) = provider;
        Dictionary<string, List<object>>? collections = null;

        try
        {
            collections = ProviderLoader.QueryCollections(instance, schema, query);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _warnings.Add($"Error querying provider '{providerName}': {ex.Message}");
            return CopNull.Instance;
        }

        if (collections is null || collections.Count == 0)
            return new CopList([]);

        // Convert raw objects to CopValues using DataObjectAdapter
        CopList ToCopList(List<object> items)
        {
            return new CopList(items
                .Select(item => (CopValue)new CopDynamicObject(item, DataObjectAdapter.Instance))
                .ToList());
        }

        // Single collection → return as CopList directly
        if (collections.Count == 1)
            return ToCopList(collections.Values.First());

        // Multiple collections → return as CopObject with named fields
        var fields = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (var (name, items) in collections)
            fields[name] = ToCopList(items);
        return new CopObject(fields);
    }
}
