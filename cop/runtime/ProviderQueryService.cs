using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using System.Text.Json;

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
    private readonly Dictionary<string, CopValue> _queryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeBindings?> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Provider, Type Type), ClrObjectAdapter> _clrAdapters = new();
    private readonly Dictionary<DataTable, RecordViewAdapter> _tableAdapters = new();
    private readonly Dictionary<string, (DataProvider Instance, ProviderSchema Schema)> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _invocationDirectory;
    private readonly IReadOnlySet<string>? _excludedDirectories;
    private Action<string>? _diagLog;

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
        _bindings[name] = instance.GetRuntimeBindings();
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
            throw new InvalidOperationException($"Provider '{providerName}' not found for path-scoped query.");
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

        var (instance, schema) = provider;
        var collections = ProviderLoader.QueryCollections(instance, schema, query);

        _diagLog?.Invoke($"[query-result] {providerName}.{query.Collection ?? "*"} returned={collections
            .Where(c => query.Collection is null || c.Key == query.Collection).Sum(c => c.Value.Count)}");
        if (collections.TryGetValue(collectionName, out var items))
        {
            _cache[key] = items;
            return items;
        }

        // Collection not returned by provider — not an error, just empty
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
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        if (query.Collection is { } requested && query.Filter is { } filter)
        {
            var filters = query.CollectionFilters is null
                ? new Dictionary<string, FilterExpression>()
                : new Dictionary<string, FilterExpression>(query.CollectionFilters);
            if (filters.TryGetValue(requested, out var existing) && existing != filter)
                filter = FilterExpression.And(existing, filter);
            filters[requested] = filter;
            query = new ProviderQuery
            {
                RootPath = query.RootPath, Collection = requested, Filter = filter,
                CollectionFilters = filters, ExcludedDirectories = query.ExcludedDirectories, Options = query.Options
            };
        }
        var key = providerName.ToUpperInvariant() + ":" + JsonSerializer.Serialize(query);
        if (_queryCache.TryGetValue(key, out var cached)) return cached;

        var (instance, schema) = provider;
        _diagLog?.Invoke($"[query] Query {providerName}.{query.Collection ?? "*"} root={query.RootPath} filter={(query.Filter is null ? "<none>" : FilterExpression.Format(query.Filter))}");
        DataStore? store = null;
        var collections = ProviderLoader.QueryCollections(instance, schema, query, onStore: value => store = value);

        _diagLog?.Invoke($"[query-result] {providerName}.{query.Collection ?? "*"} returned={collections
            .Where(c => query.Collection is null || c.Key == query.Collection).Sum(c => c.Value.Count)}");
        CopValue ToCollection(string name, List<object> items)
        {
            var values = items.Select(item => Adapt(providerName, schema, name, item, store));
            var filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            if (filter is not null)
                values = values.Where(item => CopFilter.Matches(filter, item));
            return new CopLazyCollection(() => values);
        }

        CopValue result;
        if (query.Collection is { } collection)
            result = ToCollection(collection, collections.GetValueOrDefault(collection) ?? []);
        else if (collections.Count == 1)
            result = ToCollection(collections.Keys.First(), collections.Values.First());
        else
            result = new CopObject(collections.ToDictionary(c => c.Key, c => ToCollection(c.Key, c.Value)));
        _queryCache.Add(key, result);
        return result;
    }

    /// <summary>Builds a schema-shaped provider value without executing any collection.</summary>
    public CopValue CreateValue(string providerName, ProviderQuery query)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        var schema = provider.Schema;
        CopQueryable Collection(ProviderCollectionSchema collection)
        {
            var properties = schema.Types.FirstOrDefault(t => t.Name == collection.ItemType)?.Properties
                .Where(p => !p.Collection && p.Type is "string" or "bool" or "int" or "float")
                .ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
            var value = new CopQueryable(providerName, query.ForCollection(collection.Name), QueryProvider,
                itemType: collection.ItemType, scalarProperties: properties);
            value.SetQueryLog(_diagLog);
            return value;
        }
        if (schema.Collections.Count == 1 && provider.Instance.QueryResultType is null)
        {
            var collection = schema.Collections[0];
            return Collection(collection);
        }
        var fields = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (var collection in schema.Collections)
            fields[collection.Name] = Collection(collection);
        if (provider.Instance.GetQueryMetadata(query) is { } metadata)
            foreach (var (name, value) in metadata)
                fields[name] = DataObjectAdapter.Marshal(value);
        return new CopObject(fields) { TypeName = provider.Instance.QueryResultType };
    }

    private CopValue Adapt(string providerName, ProviderSchema schema, string collectionName, object item, DataStore? store)
    {
        if (item is DataObject) return new CopDynamicObject(item, DataObjectAdapter.Instance);
        var itemType = schema.Collections.FirstOrDefault(c => c.Name == collectionName)?.ItemType;
        if (item is RecordView record && schema.Types.FirstOrDefault(t => t.Name == itemType) is { } typeSchema)
        {
            if (!_tableAdapters.TryGetValue(record.Table, out var adapter))
            {
                adapter = new RecordViewAdapter(typeSchema.Properties,
                    store?.Tables.ToDictionary(t => t.Key, t => t.Value), itemType, schema);
                _tableAdapters.Add(record.Table, adapter);
            }
            return new CopDynamicObject(item, adapter);
        }
        if (_bindings[providerName] is { } bindings)
        {
            var key = (providerName, item.GetType());
            if (_clrAdapters.TryGetValue(key, out var cached)) return new CopDynamicObject(item, cached);
            var type = bindings.ClrTypeMappings.GetValueOrDefault(item.GetType()) ?? itemType;
            if (type is not null && bindings.Accessors.TryGetValue(type, out var accessors))
            {
                var adapter = new ClrObjectAdapter(accessors, type, bindings.Accessors, bindings.ClrTypeMappings);
                _clrAdapters.Add(key, adapter);
                return new CopDynamicObject(item, adapter);
            }
        }
        return DataObjectAdapter.Marshal(item);
    }
}
