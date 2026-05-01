using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Service for querying providers with path overrides at evaluation time.
/// Caches results by (provider, collection, absolutePath) to avoid re-scanning.
/// Used by the interpreter when a collection reference has a PathOverride.
/// </summary>
public class ProviderQueryService : IProviderQueryService
{
    private readonly record struct CacheKey(string ProviderName, string CollectionName, string AbsolutePath);

    private readonly Dictionary<CacheKey, List<object>> _cache = new();
    private readonly Dictionary<string, (DataProvider Instance, ProviderSchema Schema)> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _invocationDirectory;
    private readonly IReadOnlySet<string>? _excludedDirectories;
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;

    public ProviderQueryService(string invocationDirectory, IReadOnlySet<string>? excludedDirectories = null)
    {
        _invocationDirectory = invocationDirectory;
        _excludedDirectories = excludedDirectories;
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
    /// <param name="providerName">Provider namespace (e.g., "csharp", "filesystem")</param>
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
            RequestedCollections = [collectionName],
            ExcludedDirectories = _excludedDirectories
        };

        try
        {
            var (instance, schema) = provider;

            if (instance.SupportedFormats.HasFlag(DataFormat.ObjectCollections))
            {
                var collections = instance.QueryCollections(query);
                if (collections != null && collections.TryGetValue(collectionName, out var items))
                {
                    _cache[key] = items;
                    return items;
                }
            }
            else if (instance.SupportedFormats.HasFlag(DataFormat.InMemoryDatabase))
            {
                var store = instance.QueryData(query);
                if (store.Tables.TryGetValue(collectionName, out var table))
                {
                    var views = new List<object>(table.Count);
                    for (int i = 0; i < table.Count; i++)
                        views.Add(new RecordView(table, i));
                    _cache[key] = views;
                    return views;
                }
            }
            else if (instance.SupportedFormats.HasFlag(DataFormat.Json))
            {
                var json = instance.Query(query);
                // JSON deserialization for path-scoped queries would require TypeRegistry wiring.
                // For now, warn and return empty. Built-in providers use ObjectCollections or InMemoryDatabase.
                _warnings.Add($"JSON-format providers are not yet supported for path-scoped queries (provider: '{providerName}').");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _warnings.Add($"Error querying provider '{providerName}' for collection '{collectionName}' at path '{absolutePath}': {ex.Message}");
        }

        var result = new List<object>();
        _cache[key] = result;
        return result;
    }
}
