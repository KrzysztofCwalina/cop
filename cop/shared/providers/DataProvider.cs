// Defines provider discovery, query, and schema contracts for the runtime.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Abstract base class for Cop data providers.
/// Provider DLLs contain a subclass and are loaded dynamically by the engine.
///
/// Contract:
///   GetSchema() — declares what types and collections this provider can produce (static capability).
///   QueryAsync() — returns actual data for a given context, honoring caller cancellation.
/// </summary>
public abstract class DataProvider
{
    /// <summary>
    /// Human-readable provider name, used in diagnostics.
    /// Default strips the "Provider" suffix from the class name.
    /// </summary>
    public override string ToString()
    {
        string name = GetType().Name;
        return name.EndsWith("Provider", StringComparison.Ordinal)
            ? name[..^"Provider".Length]
            : name;
    }

    /// <summary>
    /// Returns the provider schema as UTF-8 JSON.
    /// Declares the types and collections this provider can produce.
    /// Called once at load time for type registration and placeholder setup.
    /// Does not change per-query — it's the provider's static contract.
    /// </summary>
    public abstract ReadOnlyMemory<byte> GetSchema();

    /// <summary>
    /// Returns CLR runtime bindings: type mappings, lambda property accessors,
    /// collection extractors, and method evaluators. Called once at registration time.
    /// </summary>
    public virtual RuntimeBindings? GetRuntimeBindings() => null;

    /// <summary>Optional structural type name for the object containing deferred collections.</summary>
    public virtual string? QueryResultType => null;

    /// <summary>Cheap metadata fields to expose beside deferred collections. Must not query data.</summary>
    public virtual IReadOnlyDictionary<string, object?>? GetQueryMetadata(ProviderQuery query) => null;

    /// <summary>
    /// Queries for provider data. Single entry point for all data retrieval.
    /// Returns one of:
    ///   - Dictionary&lt;string, List&lt;object&gt;&gt; — named collections of CLR objects
    ///   - DataStore — binary stride-based data (high-performance path)
    ///   - null — no data (provider only contributes types/bindings/functions)
    ///
    /// Use query.Collection to select a single collection (optimization hint:
    /// providers may skip computing other collections).
    /// </summary>
    public virtual ValueTask<object?> QueryAsync(ProviderQuery query,
        CancellationToken cancellationToken = default) => ValueTask.FromResult<object?>(null);

    /// <summary>
    /// Optional pull-based query path. Providers may return DeferredProviderResult to defer
    /// file reads and parsing until enumeration. The default uses the provider's QueryAsync behavior.
    /// </summary>
    public virtual ValueTask<object?> QueryDeferredAsync(ProviderQuery query,
        CancellationToken cancellationToken = default) => QueryAsync(query, cancellationToken);

    /// <summary>Filters extracted CLR collections before returning them to the host.</summary>
    protected Dictionary<string, List<object>> ApplyQueryFilters(
        ProviderQuery query, Dictionary<string, List<object>> collections)
    {
        if (query.Filter is null && query.CollectionFilters is null)
        {
            return collections;
        }

        ProviderSchema schema = ProviderSchema.FromJson(GetSchema());
        RuntimeBindings? bindings = GetRuntimeBindings();
        foreach (string? name in collections.Keys.ToArray())
        {
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            string? type = schema.Collections.FirstOrDefault(c => c.Name == name)?.ItemType;
            if (filter is not null && type is not null
                && bindings?.Accessors.TryGetValue(type, out Dictionary<string, Func<object, object?>>? accessors) == true)
            {
                collections[name] = collections[name].Where(FilterCompiler.Compile(filter, accessors)).ToList();
            }
        }
        return collections;
    }

    /// <summary>
    /// Returns namespace-scoped functions that this provider exposes (e.g., http.Get, json.Load).
    /// Functions return ValueTask&lt;object?&gt; and accept evaluated arguments and a cancellation token.
    /// The engine registers these under the provider's namespace so they can be called as
    /// namespace.FunctionName(args) in cop scripts.
    /// </summary>
    public virtual Dictionary<string,
        Func<List<object?>, CancellationToken, ValueTask<object?>>>? GetFunctions() => null;

    /// <summary>
    /// Returns functions bound to this execution's source and comparison context.
    /// The default preserves existing providers' parameterless function contract.
    /// </summary>
    public virtual Dictionary<string,
        Func<List<object?>, CancellationToken, ValueTask<object?>>>? GetFunctions(AnalysisContext context)
        => GetFunctions();

    /// <summary>
    /// Registers additional capabilities (e.g., document loaders, file parsers) into the TypeRegistry.
    /// Called once by the runtime after schema registration.
    /// Override in providers that need to register custom loaders.
    /// </summary>
    public virtual void RegisterCapabilities(TypeRegistry registry, string rootPath) { }
}
