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
    /// Loads a provider assembly from a package directory.
    /// Validates trust, loads the DLL, instantiates the provider, and calls GetSchema().
    /// </summary>
    public static LoadedProvider? Load(string packageDir, PackageMetadata metadata, List<string> errors)
    {
        if (!metadata.IsClrProvider)
            return null;

        if (string.IsNullOrEmpty(metadata.ProviderEntry))
        {
            errors.Add($"Package '{metadata.Name}' has provider:clr but no providerEntry specified.");
            return null;
        }

        // Find the provider DLL
        var dllPath = FindProviderDll(packageDir, metadata.Name);
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
            var providerType = assembly.GetType(metadata.ProviderEntry);

            if (providerType is null)
            {
                errors.Add($"Provider entry type '{metadata.ProviderEntry}' not found in assembly '{dllPath}'.");
                return null;
            }

            if (!typeof(DataProvider).IsAssignableFrom(providerType))
            {
                errors.Add($"Provider entry type '{metadata.ProviderEntry}' does not extend DataProvider.");
                return null;
            }

            var instance = (DataProvider)Activator.CreateInstance(providerType)!;

            // Get schema
            var schema = ProviderSchema.FromJson(instance.GetSchema());

            return new LoadedProvider(instance, schema, metadata.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add($"Failed to load provider '{metadata.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Registers a provider's schema, type accessors, and runtime bindings into the type registry.
    /// Works for both built-in and external providers.
    /// </summary>
    public static ProviderSchema RegisterSchema(DataProvider instance, TypeRegistry registry)
    {
        var schema = ProviderSchema.FromJson(instance.GetSchema());
        registry.RegisterProviderSchema(schema);

        if (instance.SupportedFormats.HasFlag(DataFormat.InMemoryDatabase) ||
            instance.SupportedFormats.HasFlag(DataFormat.ObjectCollections))
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
        else if (instance.SupportedFormats.HasFlag(DataFormat.AsyncStream))
        {
            // Streaming providers use ScriptObject accessors (same as JSON)
            JsonCollectionDeserializer.RegisterScriptObjectAccessors(registry, schema);
        }
        else if (instance.SupportedFormats.HasFlag(DataFormat.Json))
        {
            JsonCollectionDeserializer.RegisterScriptObjectAccessors(registry, schema);
        }

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
    /// Queries a provider with the given query and registers the resulting collections.
    /// Collections are registered under the provider's namespace for proper scoping.
    /// </summary>
    public static void QueryAndRegister(DataProvider instance, ProviderSchema schema, string ns, TypeRegistry registry, ProviderQuery query, List<string>? errors = null)
    {
        try
        {
            if (instance.SupportedFormats.HasFlag(DataFormat.AsyncStream))
            {
                // Streaming providers don't eagerly query — register streaming sources and sinks
                foreach (var coll in schema.Collections)
                {
                    var qualifiedName = $"{ns}.{coll.Name}";
                    registry.RegisterStreamingSource(qualifiedName, new ProviderStreamingSource(instance, coll.Name));
                }
                var sinks = instance.GetSinks();
                if (sinks != null)
                    foreach (var sink in sinks)
                        registry.RegisterSink(ns, sink);
            }
            else if (instance.SupportedFormats.HasFlag(DataFormat.ObjectCollections))
            {
                var collections = instance.QueryCollections(query);
                if (collections != null)
                {
                    foreach (var (collName, items) in collections)
                        registry.AppendNamespacedCollection(ns, collName, items);
                }
            }
            else if (instance.SupportedFormats.HasFlag(DataFormat.InMemoryDatabase))
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
                    registry.AppendNamespacedCollection(ns, collName, views);
                }
            }
            else if (instance.SupportedFormats.HasFlag(DataFormat.Json))
            {
                var resultJson = instance.Query(query);
                var collections = JsonCollectionDeserializer.Deserialize(resultJson, schema);
                foreach (var (collName, items) in collections)
                    registry.AppendNamespacedCollection(ns, collName, items);
            }
            else
            {
                errors?.Add($"Provider '{instance}' does not support any query format.");
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
    /// Initializes provider capabilities (document loaders, file parsers, etc.).
    /// Providers implement <see cref="ICapabilityProvider"/> to register additional capabilities.
    /// Also registers built-in file parsers (e.g., JSON).
    /// </summary>
    public static void InitializeCapabilities(DataProvider instance, TypeRegistry registry, string rootPath)
    {
        if (instance is ICapabilityProvider cap)
            cap.RegisterCapabilities(registry, rootPath);
    }

    /// <summary>
    /// Finds the provider DLL in the package's lib/ directory.
    /// Prefers a DLL matching the package name or containing "provider".
    /// </summary>
    private static string? FindProviderDll(string packageDir, string packageName)
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

        // When multiple DLLs exist, prefer one matching the package name or containing "provider"
        var match = dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d)
            .Equals($"{packageName}-provider", StringComparison.OrdinalIgnoreCase));
        match ??= dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d)
            .Contains("provider", StringComparison.OrdinalIgnoreCase));
        return match ?? dlls[0];
    }
}

/// <summary>
/// Generic adapter that wraps a DataProvider's QueryStream as an IStreamingCollectionSource.
/// Used by the engine to register streaming collections from external AsyncStream providers.
/// </summary>
internal class ProviderStreamingSource : IStreamingCollectionSource
{
    private readonly DataProvider _provider;

    public ProviderStreamingSource(DataProvider provider, string collectionName)
    {
        _provider = provider;
        CollectionName = collectionName;
    }

    public string CollectionName { get; }

    public IAsyncEnumerable<object> QueryStream(CancellationToken cancellationToken = default)
    {
        return _provider.QueryStream(new ProviderQuery(), cancellationToken);
    }
}

/// <summary>
/// Isolated assembly load context for provider DLLs.
/// Shares Cop.Core and the shared code model assembly with the default context
/// to avoid type identity splits for DataProvider, SourceFile, etc.
/// </summary>
internal class ProviderLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly string? SharedCodeAssemblyName = typeof(CodeSchemaProvider).Assembly.GetName().Name;

    public ProviderLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share Cop.Core and the code models assembly with the default context
        if (assemblyName.Name == "Cop.Core" || assemblyName.Name == SharedCodeAssemblyName)
            return null; // falls back to default context

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
            return LoadFromAssemblyPath(assemblyPath);

        return null;
    }
}
