using System.Reflection;
using System.Runtime.Loader;
using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Loads provider assemblies in an isolated <see cref="AssemblyLoadContext"/>,
/// discovers and instantiates <see cref="ObjectProvider"/> subclasses,
/// and wires them into the Cop type system.
/// </summary>
public static class ProviderLoader
{
    /// <summary>
    /// Represents a loaded and ready-to-query provider instance with its schema.
    /// </summary>
    public record LoadedProvider(ObjectProvider Instance, ProviderSchema Schema, string PackageName);

    /// <summary>
    /// Represents a loaded streaming source provider.
    /// </summary>
    public record LoadedSourceProvider(SourceProvider Instance, ProviderSchema Schema, string PackageName);

    /// <summary>
    /// Represents a loaded sink provider.
    /// </summary>
    public record LoadedSinkProvider(SinkProvider Instance, string PackageName);

    /// <summary>
    /// Loads a provider assembly from a package directory.
    /// Validates trust, loads the DLL, instantiates the provider, and calls GetSchema().
    /// Discovers ObjectProvider, SourceProvider, and SinkProvider subclasses.
    /// </summary>
    public static LoadedProvider? Load(string packageDir, PackageMetadata metadata, List<string> errors)
    {
        return Load(packageDir, metadata, errors, out _, out _);
    }

    /// <summary>
    /// Loads a provider from a package directory.
    /// Handles CLR providers (in-process DLL) and process providers (Node.js, Python via stdin/stdout).
    /// Also outputs any SourceProvider and SinkProvider instances found (CLR only).
    /// </summary>
    public static LoadedProvider? Load(string packageDir, PackageMetadata metadata, List<string> errors,
        out List<LoadedSourceProvider> sourceProviders, out List<LoadedSinkProvider> sinkProviders)
    {
        sourceProviders = [];
        sinkProviders = [];

        // Handle out-of-process providers (Node.js, Python)
        if (metadata.IsNodeProvider || metadata.IsPythonProvider)
            return LoadProcessProvider(packageDir, metadata, errors);

        if (!metadata.IsClrProvider)
            return null;

        if (string.IsNullOrEmpty(metadata.ProviderEntry))
        {
            errors.Add($"Package '{metadata.Name}' has provider:clr but no providerEntry specified.");
            return null;
        }

        // Find the provider DLL
        var dllPath = FindProviderDll(packageDir, metadata);
        if (dllPath is null)
        {
            errors.Add($"Provider assembly not found for package '{metadata.Name}'. Expected a .dll in '{Path.Combine(packageDir, "lib")}'.");
            return null;
        }

        try
        {
            // Load in isolated context
            var alc = new ProviderLoadContext(dllPath);
            var assembly = alc.LoadFromAssemblyPath(dllPath);

            // Discover SourceProvider subclasses
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract) continue;
                if (typeof(SourceProvider).IsAssignableFrom(type))
                {
                    var instance = (SourceProvider)Activator.CreateInstance(type)!;
                    var schema = ProviderSchema.FromJson(instance.GetSchema());
                    sourceProviders.Add(new LoadedSourceProvider(instance, schema, metadata.Name));
                }
                else if (typeof(SinkProvider).IsAssignableFrom(type) &&
                         !typeof(ConsoleWriteLineSink).IsAssignableFrom(type) &&
                         !typeof(FileWriteSink).IsAssignableFrom(type) &&
                         !typeof(ListAppendSink).IsAssignableFrom(type))
                {
                    var instance = (SinkProvider)Activator.CreateInstance(type)!;
                    sinkProviders.Add(new LoadedSinkProvider(instance, metadata.Name));
                }
            }

            // If source/sink providers were found and no ObjectProvider entry, that's fine
            var providerType = assembly.GetType(metadata.ProviderEntry);
            if (providerType is null)
            {
                if (sourceProviders.Count > 0 || sinkProviders.Count > 0)
                    return null; // pure source/sink package — no ObjectProvider needed
                errors.Add($"Provider entry type '{metadata.ProviderEntry}' not found in assembly '{dllPath}'.");
                return null;
            }

            if (!typeof(ObjectProvider).IsAssignableFrom(providerType))
            {
                if (sourceProviders.Count > 0 || sinkProviders.Count > 0)
                    return null; // entry type is a SourceProvider/SinkProvider, not ObjectProvider
                errors.Add($"Provider entry type '{metadata.ProviderEntry}' does not extend ObjectProvider.");
                return null;
            }

            var dataInstance = (ObjectProvider)Activator.CreateInstance(providerType)!;
            var dataSchema = ProviderSchema.FromJson(dataInstance.GetSchema());
            return new LoadedProvider(dataInstance, dataSchema, metadata.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add($"Failed to load provider '{metadata.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Registers a provider's schema, type accessors, and runtime bindings into the type registry.
    /// Works for both built-in and external ObjectProvider instances (sync collections only).
    /// </summary>
    public static ProviderSchema RegisterSchema(ObjectProvider instance, TypeRegistry registry)
    {
        var schema = ProviderSchema.FromJson(instance.GetSchema());
        registry.RegisterProviderSchema(schema);

        if (instance.SupportedFormats.HasFlag(ObjectFormat.InMemoryDatabase) ||
            instance.SupportedFormats.HasFlag(ObjectFormat.ObjectCollections))
        {
            registry.RegisterDataTableAccessors(schema);

            var bindings = instance.GetRuntimeBindings();
            if (bindings != null)
            {
                foreach (var (clrType, copTypeName) in bindings.ClrTypeMappings)
                    registry.RegisterClrType(clrType, copTypeName);
                foreach (var (typeName, accessors) in bindings.Accessors)
                    registry.RegisterAccessors(typeName, accessors);
                if (bindings.CollectionExtractors != null)
                {
                    foreach (var (collName, extractor) in bindings.CollectionExtractors)
                        registry.RegisterCollectionExtractor(collName, doc => extractor(doc.As<SourceFile>()));
                }
                if (bindings.MethodEvaluators != null)
                {
                    foreach (var ((typeName, methodName), evaluator) in bindings.MethodEvaluators)
                        registry.RegisterMethodEvaluator(typeName, methodName, evaluator);
                }
                if (bindings.TextConverters != null)
                {
                    foreach (var (typeName, converter) in bindings.TextConverters)
                    {
                        var desc = registry.GetType(typeName);
                        if (desc != null) desc.TextConverter = converter;
                    }
                }
            }
        }
        else if (instance.SupportedFormats.HasFlag(ObjectFormat.Json))
        {
            JsonCollectionDeserializer.RegisterDataObjectAccessors(registry, schema);
        }

        return schema;
    }

    /// <summary>
    /// Registers a SourceProvider's schema (types use DataObject accessors).
    /// </summary>
    public static ProviderSchema RegisterSchema(SourceProvider instance, TypeRegistry registry)
    {
        var schema = ProviderSchema.FromJson(instance.GetSchema());
        registry.RegisterProviderSchema(schema);
        JsonCollectionDeserializer.RegisterDataObjectAccessors(registry, schema);
        return schema;
    }

    /// <summary>
    /// Queries a loaded provider and registers the resulting collections as global collections.
    /// Prefers the Objects format (in-process CLR objects) when available for better performance.
    /// Falls back to JSON format with deserialization.
    /// </summary>
    public static void QueryAndRegister(LoadedProvider provider, TypeRegistry registry, string? rootPath, List<string> errors, IReadOnlySet<string>? excludedDirectories = null)
        => QueryAndRegister(provider.Instance, provider.Schema, provider.PackageName, registry, new ProviderQuery { RootPath = rootPath, ExcludedDirectories = excludedDirectories }, errors);

    /// <summary>
    /// Registers a SourceProvider as a streaming source in the registry.
    /// </summary>
    public static void RegisterSourceProvider(SourceProvider instance, ProviderSchema schema, string ns, TypeRegistry registry)
    {
        foreach (var coll in schema.Collections)
        {
            var qualifiedName = $"{ns}.{coll.Name}";
            registry.RegisterStreamingSource(qualifiedName, instance);
        }

        // Register provider functions if any
        var providerFunctions = instance.GetProviderFunctions();
        if (providerFunctions is not null)
        {
            foreach (var (funcName, func) in providerFunctions)
                registry.RegisterProviderFunction(ns, funcName, func);
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
    public static void QueryAndRegister(ObjectProvider instance, ProviderSchema schema, string ns, TypeRegistry registry, ProviderQuery query, List<string>? errors = null)
    {
        try
        {
            if (instance.SupportedFormats.HasFlag(ObjectFormat.ObjectCollections))
            {
                var collections = instance.QueryCollections(query);
                if (collections != null)
                {
                    foreach (var (collName, items) in collections)
                    {
                        var filtered = ApplyCollectionFilter(registry, schema, collName, items, query.CollectionFilters);
                        registry.AppendNamespacedCollection(ns, collName, filtered);
                    }
                }
            }
            else if (instance.SupportedFormats.HasFlag(ObjectFormat.InMemoryDatabase))
            {
                var store = instance.QueryData(query);

                // Wire collection/reference accessors using actual DataStore tables
                registry.WireDataStoreAccessors(schema, store);

                // Register only top-level collections (those declared in the schema)
                var schemaCollections = new HashSet<string>(schema.Collections.Select(c => c.Name));
                foreach (var (collName, table) in store.Tables)
                {
                    if (!schemaCollections.Contains(collName)) continue;
                    var views = new List<object>(table.Count);
                    for (int i = 0; i < table.Count; i++)
                        views.Add(new RecordView(table, i));
                    var filtered = ApplyCollectionFilter(registry, schema, collName, views, query.CollectionFilters);
                    registry.AppendNamespacedCollection(ns, collName, filtered);
                }
            }
            else if (instance.SupportedFormats.HasFlag(ObjectFormat.Json))
            {
                var resultJson = instance.Query(query);
                var collections = JsonCollectionDeserializer.Deserialize(resultJson, schema);
                foreach (var (collName, items) in collections)
                {
                    var filtered = ApplyCollectionFilter(registry, schema, collName, items, query.CollectionFilters);
                    registry.AppendNamespacedCollection(ns, collName, filtered);
                }
            }
            else
            {
                errors?.Add($"Provider '{instance}' does not support any query format.");
            }

            // Register provider functions (e.g., http.Get, http.Post) regardless of data format
            var providerFunctions = instance.GetProviderFunctions();
            if (providerFunctions is not null)
            {
                foreach (var (funcName, func) in providerFunctions)
                    registry.RegisterProviderFunction(ns, funcName, func);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (errors != null)
                errors.Add($"Provider '{instance}' query failed: {ex.Message}");
            else
                throw;
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
        if (collectionFilters is null) return items;
        if (!collectionFilters.TryGetValue(collName, out var filter)) return items;

        var itemType = schema.Collections
            .FirstOrDefault(c => c.Name.Equals(collName, StringComparison.Ordinal))?.ItemType;
        if (itemType is null) return items;

        return registry.ApplyPushdownFilter(itemType, items, filter);
    }

    /// <summary>
    /// Initializes provider capabilities (document loaders, file parsers, etc.).
    /// Providers implement <see cref="ICapabilityProvider"/> to register additional capabilities.
    /// Also registers built-in file parsers (e.g., JSON).
    /// </summary>
    public static void InitializeCapabilities(ObjectProvider instance, TypeRegistry registry, string rootPath)
    {
        if (instance is ICapabilityProvider cap)
            cap.RegisterCapabilities(registry, rootPath);
    }

    /// <summary>
    /// Loads a process-based provider (Node.js or Python).
    /// Creates a ProcessObjectProvider that communicates via stdin/stdout.
    /// </summary>
    private static LoadedProvider? LoadProcessProvider(string packageDir, PackageMetadata metadata, List<string> errors)
    {
        if (string.IsNullOrEmpty(metadata.ProviderEntry))
        {
            errors.Add($"Package '{metadata.Name}' has provider:{metadata.Provider} but no providerEntry specified.");
            return null;
        }

        var runtime = metadata.IsNodeProvider ? "node" : "python";
        var entryScript = metadata.ProviderEntry;

        // Verify the entry script exists
        var scriptPath = Path.Combine(packageDir, entryScript);
        if (!File.Exists(scriptPath))
        {
            errors.Add($"Provider entry script not found for package '{metadata.Name}': '{scriptPath}'");
            return null;
        }

        try
        {
            var instance = new ProcessObjectProvider(runtime, entryScript, packageDir);
            var schemaBytes = instance.GetSchema();
            var schema = ProviderSchema.FromJson(schemaBytes);
            return new LoadedProvider(instance, schema, metadata.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add($"Failed to load {runtime} provider '{metadata.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Finds the provider DLL in the package's lib/ directory.
    /// Uses the providerAssembly manifest field if specified, otherwise expects a single DLL.
    /// </summary>
    private static string? FindProviderDll(string packageDir, PackageMetadata metadata)
    {
        var libDir = Path.Combine(packageDir, "lib");
        if (!Directory.Exists(libDir))
            return null;

        var dlls = Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly);
        if (dlls.Length == 0)
        {
            // Check RID-specific subdirectories
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            var ridDir = Path.Combine(libDir, rid);
            if (Directory.Exists(ridDir))
                dlls = Directory.GetFiles(ridDir, "*.dll", SearchOption.TopDirectoryOnly);
        }

        if (dlls.Length == 0)
            return null;
        if (dlls.Length == 1)
            return dlls[0];

        // Use providerAssembly from manifest to select the correct DLL
        if (!string.IsNullOrEmpty(metadata.ProviderAssembly))
        {
            var match = dlls.FirstOrDefault(d => Path.GetFileName(d)
                .Equals(metadata.ProviderAssembly, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        // Fallback: prefer DLL matching the package name
        var nameMatch = dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d)
            .Equals(metadata.Name, StringComparison.OrdinalIgnoreCase));
        return nameMatch ?? dlls[0];
    }
}

/// <summary>
/// Isolated assembly load context for provider DLLs.
/// Shares the host assembly (cop.exe) with the default context
/// to avoid type identity splits for ObjectProvider, SourceFile, etc.
/// </summary>
internal class ProviderLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly string HostAssemblyName = typeof(ObjectProvider).Assembly.GetName().Name!;

    public ProviderLoadContext(string pluginPath) : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the host assembly with the default context so provider types
        // (ObjectProvider, SourceFile, etc.) have the same identity in both contexts
        if (assemblyName.Name == HostAssemblyName)
            return null; // falls back to default context

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
            return LoadFromAssemblyPath(assemblyPath);

        return null;
    }
}
