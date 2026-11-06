// Routes runtime collection queries to the providers registered with the engine.

using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Cop.Providers;

/// <summary>
/// Service for querying providers with path overrides at evaluation time.
/// Caches results by (provider, collection, absolutePath) to avoid re-scanning.
/// Used by the interpreter when a collection reference has a PathOverride.
/// </summary>
public class ProviderQueryService : IAsyncDisposable
{
    private readonly record struct CacheKey(string ProviderName, string CollectionName, string AbsolutePath);

    private readonly Dictionary<CacheKey, List<object>> _cache = new();
    private readonly Dictionary<string, CopValue> _queryCache = new(StringComparer.Ordinal);
    private readonly QueryReuseScope _queryReuse = new();
    private readonly Dictionary<string, RuntimeBindings?> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Provider, Type Type), ClrObjectAdapter> _clrAdapters = new();
    private readonly Dictionary<DataTable, RecordViewAdapter> _tableAdapters = new();
    private readonly Dictionary<string, (DataProvider Instance, ProviderSchema Schema)> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<object> _ownedProviders =
        new(ReferenceEqualityComparer.Instance);
    private readonly string _invocationDirectory;
    private readonly IReadOnlySet<string>? _excludedDirectories;
    private Action<string>? _diagLog;

    /// <summary>Creates a query cache rooted at the invocation directory.</summary>
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

    /// <summary>Registers a provider and assumes ownership of its disposable resources.</summary>
    public void RegisterOwnedProvider(ProviderLoader.LoadedProvider provider)
    {
        RegisterOwnedResource(provider.Instance);
        RegisterProvider(provider.PackageName, provider.Instance, provider.Schema);
    }

    internal void RegisterOwnedResource(object instance)
    {
        if (instance is IDisposable or IAsyncDisposable)
        {
            _ownedProviders.Add(instance);
        }
    }

    /// <summary>Releases owned providers without disposing borrowed built-in providers.</summary>
    public async ValueTask DisposeAsync()
    {
        object[] providers = _ownedProviders.ToArray();
        _ownedProviders.Clear();
        await Task.WhenAll(providers.Select(
            provider => ProviderLoader.DisposeProviderAsync(provider).AsTask()));
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
    public async ValueTask<List<object>> QueryAsync(
        string providerName, string collectionName, string pathOverride,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Resolve relative path against invocation directory (process CWD, not -t root)
        string absolutePath = Path.IsPathRooted(pathOverride)
            ? Path.GetFullPath(pathOverride)
            : Path.GetFullPath(Path.Combine(_invocationDirectory, pathOverride));

        CacheKey key = new CacheKey(providerName, collectionName, absolutePath);

        if (_cache.TryGetValue(key, out List<object>? cached))
        {
            return cached;
        }

        if (!_providers.TryGetValue(providerName, out (DataProvider Instance, ProviderSchema Schema) provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found for path-scoped query.");
        }

        if (!Directory.Exists(absolutePath))
        {
            throw new InvalidOperationException($"Directory '{pathOverride}' not found.");
        }

        ProviderQuery query = new ProviderQuery
        {
            RootPath = absolutePath,
            Collection = collectionName,
            ExcludedDirectories = _excludedDirectories
        };

        _diagLog?.Invoke($"[diag] Path-scoped query: {providerName}.{collectionName} RootPath={absolutePath}");

        (DataProvider? instance, ProviderSchema? schema) = provider;
        Dictionary<string, List<object>> collections = await ProviderLoader.QueryCollectionsAsync(
            instance, schema, query, cancellationToken: cancellationToken);

        _diagLog?.Invoke($"[query-result] {providerName}.{query.Collection ?? "*"} returned={collections
            .Where(c => query.Collection is null || c.Key == query.Collection).Sum(c => c.Value.Count)}");
        if (collections.TryGetValue(collectionName, out List<object>? items))
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
    public async ValueTask<CopValue> QueryProviderAsync(string providerName,
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_providers.TryGetValue(providerName, out (DataProvider Instance, ProviderSchema Schema) provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        if (query.Collection is { } requested && query.Filter is { } filter)
        {
            Dictionary<string, FilterExpression> filters = query.CollectionFilters is null
                ? new Dictionary<string, FilterExpression>()
                : new Dictionary<string, FilterExpression>(query.CollectionFilters);
            if (filters.TryGetValue(requested, out FilterExpression? existing) && existing != filter)
            {
                filter = FilterExpression.And(existing, filter);
            }

            filters[requested] = filter;
            query = new ProviderQuery
            {
                RootPath = query.RootPath, Collection = requested, Filter = filter,
                CollectionFilters = filters, ExcludedDirectories = query.ExcludedDirectories, Options = query.Options
            };
        }
        string key = providerName.ToUpperInvariant() + ":" + JsonSerializer.Serialize(query);
        if (_queryCache.TryGetValue(key, out CopValue? cached))
        {
            return cached;
        }

        (DataProvider? instance, ProviderSchema? schema) = provider;
        _diagLog?.Invoke($"[query] Query {providerName}.{query.Collection ?? "*"} root={query.RootPath} filter={(query.Filter is null ? "<none>" : FilterExpression.Format(query.Filter))}");
        DataStore? store = null;
        IReadOnlyDictionary<string, IAsyncEnumerable<object>> collections =
            await ProviderLoader.QueryDeferredCollectionsAsync(instance, schema, query,
                onStore: value => store = value, cancellationToken: cancellationToken);

        async IAsyncEnumerable<CopValue> Enumerate(string name,
            IAsyncEnumerable<object> items,
            [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
        {
            int count = 0;
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(name) ??
                query.Filter;
            await foreach (object item in items.WithCancellation(enumerationCancellation))
            {
                count++;
                CopValue value = Adapt(providerName, schema, name, item, store);
                if (filter is null ||
                    await CopFilter.MatchesAsync(filter, value, enumerationCancellation))
                {
                    yield return value;
                }
            }
            _diagLog?.Invoke($"[query-result] {providerName}.{name} returned={count}");
        }
        CopValue ToCollection(string name, IAsyncEnumerable<object> items)
        {
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            return new CopLazyCollection(token => Enumerate(name, items, token))
                { AppliedFilter = filter };
        }

        CopValue result;
        if (query.Collection is { } collection)
        {
            result = ToCollection(collection, collections.GetValueOrDefault(collection) ??
                AsyncEnumerable.Empty<object>());
        }
        else if (collections.Count == 1)
        {
            result = ToCollection(collections.Keys.First(), collections.Values.First());
        }
        else
        {
            result = new CopObject(collections.ToDictionary(c => c.Key, c => ToCollection(c.Key, c.Value)));
        }

        _queryCache.Add(key, result);
        return result;
    }

    /// <summary>Builds a schema-shaped provider value without executing any collection.</summary>
    public CopValue CreateValue(string providerName, ProviderQuery query)
    {
        if (!_providers.TryGetValue(providerName, out (DataProvider Instance, ProviderSchema Schema) provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        ProviderSchema schema = provider.Schema;
        CopQueryable Collection(ProviderCollectionSchema collection)
        {
            Dictionary<string, string>? properties = schema.Types.FirstOrDefault(t => t.Name == collection.ItemType)?.Properties
                .Where(p => !p.IsCollection && p.Type is "string" or "bool" or "int" or "float")
                .ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
            CopQueryable value = new CopQueryable(
                providerName, query.ForCollection(collection.Name), QueryProviderAsync,
                new CopQueryableOptions
                {
                    ItemType = collection.ItemType,
                    ScalarProperties = properties
                });
            value.SetQueryLog(_diagLog);
            value.SetReuseScope(_queryReuse);
            return value;
        }
        if (schema.Collections.Count == 1 && provider.Instance.QueryResultType is null)
        {
            ProviderCollectionSchema collection = schema.Collections[0];
            return Collection(collection);
        }
        Dictionary<string, CopValue> fields = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (ProviderCollectionSchema collection in schema.Collections)
        {
            fields[collection.Name] = Collection(collection);
        }

        if (provider.Instance.GetQueryMetadata(query) is { } metadata)
        {
            foreach ((string? name, object? value) in metadata)
            {
                fields[name] = DataObjectAdapter.Marshal(value);
            }
        }

        return new CopObject(fields) { TypeName = provider.Instance.QueryResultType };
    }

    private CopValue Adapt(string providerName, ProviderSchema schema, string collectionName, object item, DataStore? store)
    {
        if (item is DataObject)
        {
            return new CopDynamicObject(item, DataObjectAdapter.Instance);
        }

        string? itemType = schema.Collections.FirstOrDefault(c => c.Name == collectionName)?.ItemType;
        if (item is RecordView record && schema.Types.FirstOrDefault(t => t.Name == itemType) is { } typeSchema)
        {
            if (!_tableAdapters.TryGetValue(record.Table, out RecordViewAdapter? adapter))
            {
                adapter = new RecordViewAdapter(typeSchema.Properties,
                    store?.Tables.ToDictionary(t => t.Key, t => t.Value), itemType, schema);
                _tableAdapters.Add(record.Table, adapter);
            }
            return new CopDynamicObject(item, adapter);
        }
        if (_bindings[providerName] is { } bindings)
        {
            (string providerName, Type) key = (providerName, item.GetType());
            if (_clrAdapters.TryGetValue(key, out ClrObjectAdapter? cached))
            {
                return new CopDynamicObject(item, cached);
            }

            string? type = bindings.ClrTypeMappings.GetValueOrDefault(item.GetType()) ?? itemType;
            if (type is not null && bindings.Accessors.TryGetValue(type, out Dictionary<string, Func<object, object?>>? accessors))
            {
                ClrObjectAdapter adapter = new ClrObjectAdapter(accessors, type,
                    bindings.Accessors, bindings.ClrTypeMappings, bindings.AsyncAccessors);
                _clrAdapters.Add(key, adapter);
                return new CopDynamicObject(item, adapter);
            }
        }
        return DataObjectAdapter.Marshal(item);
    }
}
