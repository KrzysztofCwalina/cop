// Loads provider assemblies in an isolated AssemblyLoadContext, discovers and
// instantiates DataProvider subclasses, and wires them into the Cop type system.

using System.Reflection;
using System.Runtime.Loader;
using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Loads provider assemblies in an isolated <see cref="AssemblyLoadContext"/>,
/// discovers and instantiates <see cref="DataProvider"/> subclasses,
/// and wires them into the Cop type system.
/// </summary>
public static class ProviderLoader
{
    /// <summary>
    /// Represents a loaded and ready-to-query provider instance with its schema.
    /// </summary>
    public record LoadedProvider(DataProvider Instance, ProviderSchema Schema, string PackageName);

    /// <summary>
    /// Represents a loaded streaming source provider.
    /// </summary>
    public record LoadedStreamProvider(StreamProvider Instance, ProviderSchema Schema, string PackageName);

    /// <summary>
    /// Represents a loaded sink provider.
    /// </summary>
    public record LoadedSinkProvider(SinkProvider Instance, string PackageName);

    /// <summary>
    /// Loads a provider assembly from a package directory.
    /// Validates trust, loads the DLL, instantiates the provider, and calls GetSchema().
    /// Discovers DataProvider, StreamProvider, and SinkProvider subclasses.
    /// </summary>
    public static async ValueTask<(LoadedProvider? Data,
        List<LoadedStreamProvider> Sources, List<LoadedSinkProvider> Sinks)> LoadAsync(
        string packageDir, PackageMetadata metadata, List<string> errors,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.IsNodeProvider || metadata.IsPythonProvider)
        {
            LoadedProvider? process = await LoadProcessProviderAsync(
                packageDir, metadata, errors, cancellationToken);
            return (process, [], []);
        }

        List<object> instances = [];
        bool transferred = false;
        int originalErrors = errors.Count;
        try
        {
            LoadedProvider? data = LoadClrProvider(packageDir, metadata, errors,
                instances, out List<LoadedStreamProvider> sources,
                out List<LoadedSinkProvider> sinks);
            if (errors.Count != originalErrors)
            {
                return (null, [], []);
            }
            transferred = true;
            return (data, sources, sinks);
        }
        finally
        {
            if (!transferred)
            {
                await Task.WhenAll(instances.Select(
                    instance => DisposeProviderAsync(instance).AsTask()));
            }
        }
    }

    /// <summary>
    /// Loads a provider from a package directory.
    /// Handles CLR providers (in-process DLL) and process providers (Node.js, Python via stdin/stdout).
    /// Also outputs any StreamProvider and SinkProvider instances found (CLR only).
    /// </summary>
    private static LoadedProvider? LoadClrProvider(
        string packageDir, PackageMetadata metadata, List<string> errors,
        List<object> instances,
        out List<LoadedStreamProvider> sourceProviders, out List<LoadedSinkProvider> sinkProviders)
    {
        sourceProviders = [];
        sinkProviders = [];

        if (!metadata.IsClrProvider)
        {
            return null;
        }

        if (string.IsNullOrEmpty(metadata.ProviderEntry))
        {
            errors.Add($"Package '{metadata.Name}' has provider:clr but no providerEntry specified.");
            return null;
        }

        // Find the provider DLL
        string? dllPath = ProviderCompatibility.FindAssembly(packageDir, metadata);
        if (dllPath is null)
        {
            errors.Add($"Provider assembly not found for package '{metadata.Name}'. Expected a .dll in '{Path.Combine(packageDir, "lib")}'.");
            return null;
        }

        try
        {
            if (ProviderCompatibility.GetError(packageDir, metadata) is { } compatibilityError)
            {
                errors.Add(compatibilityError);
                return null;
            }

            // Load in isolated context
            ProviderLoadContext alc = new ProviderLoadContext(dllPath);
            Assembly assembly = alc.LoadFromAssemblyPath(dllPath);

            // Discover StreamProvider subclasses
            foreach (Type type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract)
                {
                    continue;
                }

                if (typeof(StreamProvider).IsAssignableFrom(type))
                {
                    StreamProvider instance = (StreamProvider)Activator.CreateInstance(type)!;
                    instances.Add(instance);
                    ProviderSchema schema = ProviderSchema.FromJson(instance.GetSchema());
                    sourceProviders.Add(new LoadedStreamProvider(instance, schema, metadata.Name));
                }
                else if (typeof(SinkProvider).IsAssignableFrom(type) &&
                         !typeof(ConsoleWriteLineSink).IsAssignableFrom(type) &&
                         !typeof(FileWriteSink).IsAssignableFrom(type) &&
                         !typeof(ListAppendSink).IsAssignableFrom(type))
                {
                    SinkProvider instance = (SinkProvider)Activator.CreateInstance(type)!;
                    instances.Add(instance);
                    sinkProviders.Add(new LoadedSinkProvider(instance, metadata.Name));
                }
            }

            // If source/sink providers were found and no DataProvider entry, that's fine
            Type? providerType = assembly.GetType(metadata.ProviderEntry);
            if (providerType is null)
            {
                if (sourceProviders.Count > 0 || sinkProviders.Count > 0)
                {
                    return null; // pure source/sink package — no DataProvider needed
                }

                errors.Add($"Provider entry type '{metadata.ProviderEntry}' not found in assembly '{dllPath}'.");
                return null;
            }

            if (!typeof(DataProvider).IsAssignableFrom(providerType))
            {
                if (sourceProviders.Count > 0 || sinkProviders.Count > 0)
                {
                    return null; // entry type is a StreamProvider/SinkProvider, not DataProvider
                }

                errors.Add($"Provider entry type '{metadata.ProviderEntry}' does not extend DataProvider.");
                return null;
            }

            DataProvider dataInstance = (DataProvider)Activator.CreateInstance(providerType)!;
            instances.Add(dataInstance);
            ProviderSchema dataSchema = ProviderSchema.FromJson(dataInstance.GetSchema());
            return new LoadedProvider(dataInstance, dataSchema, metadata.Name);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or
            ReflectionTypeLoadException or TypeLoadException or MemberAccessException or
            InvalidOperationException or ArgumentException or System.Text.Json.JsonException or
            System.Security.SecurityException ||
            ex is TargetInvocationException { InnerException: not OperationCanceledException
                and not OutOfMemoryException })
        {
            string detail = ex.Message;
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = ex.InnerException?.Message ?? $"{ex.GetType().Name} (no detail)";
            }

            if (ex.InnerException != null && !string.IsNullOrWhiteSpace(ex.InnerException.Message) && !detail.Contains(ex.InnerException.Message))
            {
                detail += $" -> {ex.InnerException.Message}";
            }

            errors.Add($"Failed to load provider '{metadata.Name}' from '{dllPath}': {detail}");
            return null;
        }
    }

    /// <summary>
    /// Registers a provider's schema, type accessors, and runtime bindings into the type registry.
    /// Works for both built-in and external DataProvider instances.
    /// </summary>
    public static ProviderSchema RegisterSchema(DataProvider instance, TypeRegistry registry)
    {
        ProviderSchema schema = ProviderSchema.FromJson(instance.GetSchema());
        registry.RegisterProviderSchema(schema);

        RuntimeBindings? bindings = instance.GetRuntimeBindings();
        if (bindings != null)
        {
            foreach ((Type? clrType, string? copTypeName) in bindings.ClrTypeMappings)
            {
                registry.RegisterClrType(clrType, copTypeName);
            }

            foreach ((string? typeName, Dictionary<string, Func<object, object?>>? accessors) in bindings.Accessors)
            {
                registry.RegisterAccessors(typeName, accessors);
            }

            foreach ((string typeName, Dictionary<string, Func<object, CancellationToken,
                ValueTask<object?>>> accessors) in bindings.AsyncAccessors)
            {
                registry.RegisterAsyncAccessors(typeName, accessors);
            }

            if (bindings.CollectionExtractors != null)
            {
                foreach ((string? collName, Func<object, List<object>>? extractor) in bindings.CollectionExtractors)
                {
                    registry.RegisterCollectionExtractor(collName, doc => extractor(doc.As<SourceFile>()));
                }
            }
            if (bindings.MethodEvaluators != null)
            {
                foreach (((string? typeName, string? methodName), Func<object, List<object?>, object?>? evaluator) in bindings.MethodEvaluators)
                {
                    registry.RegisterMethodEvaluator(typeName, methodName, evaluator);
                }
            }
            if (bindings.TextConverters != null)
            {
                foreach ((string? typeName, Func<object, string>? converter) in bindings.TextConverters)
                {
                    TypeDescriptor? desc = registry.GetType(typeName);
                    if (desc != null)
                    {
                        desc.TextConverter = converter;
                    }
                }
            }
        }

        return schema;
    }

    internal static async ValueTask<Dictionary<string, List<object>>> QueryCollectionsAsync(
        DataProvider instance, ProviderSchema schema, ProviderQuery query,
        TypeRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        object? result = await instance.QueryAsync(query, cancellationToken);
        return await MaterializeCollectionsAsync(result, schema, registry, null, cancellationToken);
    }

    internal static async ValueTask<IReadOnlyDictionary<string, IAsyncEnumerable<object>>>
        QueryDeferredCollectionsAsync(DataProvider instance, ProviderSchema schema,
            ProviderQuery query, Action<DataStore>? onStore = null,
            CancellationToken cancellationToken = default)
    {
        object? result = await instance.QueryDeferredAsync(query, cancellationToken);
        if (result is DeferredProviderResult deferred)
        {
            return deferred.Collections;
        }
        Dictionary<string, List<object>> buffered = await MaterializeCollectionsAsync(
            result, schema, null, onStore, cancellationToken);
        return buffered.ToDictionary(collection => collection.Key,
            collection => collection.Value.ToAsyncEnumerable(), StringComparer.Ordinal);
    }

    private static async ValueTask<Dictionary<string, List<object>>> MaterializeCollectionsAsync(
        object? result, ProviderSchema schema, TypeRegistry? registry,
        Action<DataStore>? onStore, CancellationToken cancellationToken)
    {
        if (result is DeferredProviderResult deferred)
        {
            Dictionary<string, List<object>> collections = new(StringComparer.Ordinal);
            foreach ((string name, IAsyncEnumerable<object> values) in deferred.Collections)
            {
                List<object> items = [];
                await foreach (object item in values.WithCancellation(cancellationToken))
                {
                    items.Add(item);
                }
                collections.Add(name, items);
            }
            return collections;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result switch
        {
            null => new Dictionary<string, List<object>>(StringComparer.Ordinal),
            Dictionary<string, List<object>> collections => collections,
            DataStore store => ConvertDataStoreToCollections(schema, store, registry, onStore),
            byte[] json => ConvertJsonToCollections(schema, json, registry),
            ReadOnlyMemory<byte> json => ConvertJsonToCollections(schema, json.ToArray(), registry),
            _ => throw new InvalidOperationException(
                $"Provider query returned unsupported result type '{result.GetType().FullName}'.")
        };
    }

    private static Dictionary<string, List<object>> ConvertDataStoreToCollections(ProviderSchema schema, DataStore store, TypeRegistry? registry, Action<DataStore>? onStore)
    {
        onStore?.Invoke(store);
        registry?.RegisterDataTableAccessors(schema);
        registry?.WireDataStoreAccessors(schema, store);

        Dictionary<string, List<object>> collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        HashSet<string> topLevelCollections = new HashSet<string>(schema.Collections.Select(c => c.Name), StringComparer.Ordinal);
        foreach ((string? collectionName, DataTable? table) in store.Tables)
        {
            if (!topLevelCollections.Contains(collectionName))
            {
                continue;
            }

            List<object> items = new List<object>(table.Count);
            for (int i = 0; i < table.Count; i++)
            {
                items.Add(new RecordView(table, i));
            }

            collections[collectionName] = items;
        }

        return collections;
    }

    private static Dictionary<string, List<object>> ConvertJsonToCollections(ProviderSchema schema, byte[] json, TypeRegistry? registry)
    {
        if (registry is not null)
        {
            JsonCollectionDeserializer.RegisterDataObjectAccessors(registry, schema);
        }

        return JsonCollectionDeserializer.Deserialize(json, schema);
    }

    /// <summary>
    /// Registers a StreamProvider's schema (types use DataObject accessors).
    /// </summary>
    public static ProviderSchema RegisterSchema(StreamProvider instance, TypeRegistry registry)
    {
        ProviderSchema schema = ProviderSchema.FromJson(instance.GetSchema());
        registry.RegisterProviderSchema(schema);
        JsonCollectionDeserializer.RegisterDataObjectAccessors(registry, schema);
        return schema;
    }

    /// <summary>
    /// Queries a loaded provider and registers the resulting collections as global collections.
    /// Prefers the Objects format (in-process CLR objects) when available for better performance.
    /// Falls back to JSON format with deserialization.
    /// </summary>
    public static ValueTask QueryAndRegisterAsync(LoadedProvider provider,
        TypeRegistry registry, string? rootPath, List<string> errors,
        IReadOnlySet<string>? excludedDirectories = null)
        => QueryAndRegisterAsync(provider, registry,
            new ProviderQuery { RootPath = rootPath, ExcludedDirectories = excludedDirectories }, errors);

    /// <summary>
    /// Registers a StreamProvider as a streaming source in the registry.
    /// </summary>
    public static void RegisterStreamProvider(StreamProvider instance, ProviderSchema schema, string ns, TypeRegistry registry)
    {
        foreach (ProviderCollectionSchema coll in schema.Collections)
        {
            string qualifiedName = $"{ns}.{coll.Name}";
            registry.RegisterStreamingSource(qualifiedName, instance);
        }
    }

    /// <summary>
    /// Registers a SinkProvider in the registry under the given namespace.
    /// </summary>
    public static void RegisterSinkProvider(SinkProvider instance, string ns, TypeRegistry registry)
    {
        registry.RegisterSink(ns, instance);
    }

    /// <summary>
    /// Queries a provider with the given query and registers the resulting collections.
    /// Collections are registered under the provider's namespace for proper scoping.
    /// When CollectionFilters are specified, items are filtered before registration
    /// using compiled filter predicates for efficient pushdown.
    /// </summary>
    public static async ValueTask QueryAndRegisterAsync(LoadedProvider provider,
        TypeRegistry registry, ProviderQuery query, List<string>? errors = null,
        CancellationToken cancellationToken = default)
    {
        (DataProvider instance, ProviderSchema schema, string ns) = provider;
        try
        {
            Dictionary<string, List<object>> collections = await QueryCollectionsAsync(
                instance, schema, query, registry, cancellationToken);
            foreach ((string? collName, List<object>? items) in collections)
            {
                List<object> filtered = ApplyCollectionFilter(registry, schema, collName, items, query.CollectionFilters);
                registry.AppendNamespacedCollection(ns, collName, filtered);
            }
        }
        catch (Exception ex) when (ex is Cop.Lang.Interpreter.CopEvaluationException or
            IOException or UnauthorizedAccessException or
            System.Security.SecurityException or HttpRequestException or TimeoutException or
            ArgumentException or InvalidOperationException or NotSupportedException or
            FormatException or System.Text.Json.JsonException)
        {
            if (errors != null)
            {
                errors.Add($"Provider '{instance}' query failed: {ex.Message}");
            }
            else
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Applies a per-collection pushdown filter if one exists for this collection.
    /// Uses FilterCompiler which gracefully ignores unknown properties.
    /// </summary>
    private static List<object> ApplyCollectionFilter(
        TypeRegistry registry, ProviderSchema schema, string collName,
        List<object> items, IReadOnlyDictionary<string, FilterExpression>? collectionFilters)
    {
        if (collectionFilters is null)
        {
            return items;
        }

        if (!collectionFilters.TryGetValue(collName, out FilterExpression? filter))
        {
            return items;
        }

        string? itemType = schema.Collections
            .FirstOrDefault(c => c.Name.Equals(collName, StringComparison.Ordinal))?.ItemType;
        if (itemType is null)
        {
            return items;
        }

        return registry.ApplyPushdownFilter(itemType, items, filter);
    }

    /// <summary>
    /// Initializes provider capabilities (document loaders, file parsers, etc.).
    /// Calls the provider's RegisterCapabilities virtual method.
    /// </summary>
    public static void InitializeCapabilities(DataProvider instance, TypeRegistry registry, string rootPath)
    {
        instance.RegisterCapabilities(registry, rootPath);
    }

    /// <summary>
    /// Loads a process-based provider (Node.js or Python).
    /// Creates a process-backed DataProvider that communicates via stdin/stdout.
    /// </summary>
    private static async ValueTask<LoadedProvider?> LoadProcessProviderAsync(
        string packageDir, PackageMetadata metadata, List<string> errors,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(metadata.ProviderEntry))
        {
            errors.Add($"Package '{metadata.Name}' has provider:{metadata.Provider} but no providerEntry specified.");
            return null;
        }

        string runtime = metadata.IsNodeProvider ? "node" : "python";
        string entryScript = metadata.ProviderEntry;

        // Verify the entry script exists
        string scriptPath = Path.Combine(packageDir, entryScript);
        if (!File.Exists(scriptPath))
        {
            errors.Add($"Provider entry script not found for package '{metadata.Name}': '{scriptPath}'");
            return null;
        }

        ProcessObjectProvider instance = new ProcessObjectProvider(
            runtime, entryScript, packageDir);
        bool isTransferred = false;
        try
        {
            await instance.InitializeAsync(cancellationToken);
            ReadOnlyMemory<byte> schemaBytes = instance.GetSchema();
            ProviderSchema schema = ProviderSchema.FromJson(schemaBytes);
            isTransferred = true;
            return new LoadedProvider(instance, schema, metadata.Name);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
            TimeoutException or System.Text.Json.JsonException or ArgumentException)
        {
            errors.Add($"Failed to load {runtime} provider '{metadata.Name}': {ex.Message}");
            return null;
        }
        finally
        {
            if (!isTransferred)
            {
                await instance.DisposeAsync();
            }
        }
    }

    internal static async ValueTask DisposeProviderAsync(object instance)
    {
        if (instance is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync();
        }
        else if (instance is IDisposable synchronous)
        {
            synchronous.Dispose();
        }
    }

}
