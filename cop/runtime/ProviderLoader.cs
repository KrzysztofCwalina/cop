using System.Reflection;
using System.Runtime.Loader;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Loads provider assemblies in an isolated <see cref="AssemblyLoadContext"/>,
/// discovers and instantiates <see cref="CopProvider"/> subclasses,
/// and wires them into the Cop type system.
/// </summary>
public static class ProviderLoader
{
    /// <summary>
    /// Represents a loaded and ready-to-query provider instance with its schema.
    /// </summary>
    public record LoadedProvider(CopProvider Instance, ProviderSchema Schema, string PackageName);

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

            if (!typeof(CopProvider).IsAssignableFrom(providerType))
            {
                errors.Add($"Provider entry type '{metadata.ProviderEntry}' does not extend CopProvider.");
                return null;
            }

            var instance = (CopProvider)Activator.CreateInstance(providerType)!;

            // Get schema
            var schemaJson = instance.GetSchema();
            var schema = ProviderSchema.FromJson(schemaJson);

            return new LoadedProvider(instance, schema, metadata.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add($"Failed to load provider '{metadata.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Queries a loaded provider and registers the resulting collections as global collections.
    /// </summary>
    public static void QueryAndRegister(LoadedProvider provider, TypeRegistry registry, string? codebasePath, List<string> errors)
    {
        if (!provider.Instance.SupportedFormats.HasFlag(ProviderFormat.Json))
        {
            errors.Add($"Provider '{provider.PackageName}' does not support JSON queries.");
            return;
        }

        try
        {
            var query = new ProviderQuery
            {
                CodebasePath = codebasePath,
            };

            var resultJson = provider.Instance.QueryJson(query);
            var collections = JsonCollectionDeserializer.Deserialize(resultJson, provider.Schema);

            foreach (var (collName, items) in collections)
            {
                registry.RegisterGlobalCollection(collName, items);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add($"Provider '{provider.PackageName}' query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the provider DLL in the package's lib/ directory.
    /// </summary>
    private static string? FindProviderDll(string packageDir, string packageName)
    {
        var libDir = Path.Combine(packageDir, "lib");
        if (!Directory.Exists(libDir))
            return null;

        // Look for any .dll file in lib/
        var dlls = Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly);
        if (dlls.Length > 0)
            return dlls[0];

        // Also check RID-specific subdirectories
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var ridDir = Path.Combine(libDir, rid);
        if (Directory.Exists(ridDir))
        {
            dlls = Directory.GetFiles(ridDir, "*.dll", SearchOption.TopDirectoryOnly);
            if (dlls.Length > 0)
                return dlls[0];
        }

        return null;
    }
}

/// <summary>
/// Isolated assembly load context for provider DLLs.
/// Shares the Cop.Core assembly with the default context to avoid type identity split.
/// </summary>
internal class ProviderLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ProviderLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share Cop.Core with the default context to prevent type identity split
        if (assemblyName.Name == "Cop.Core")
            return null; // falls back to default context

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
            return LoadFromAssemblyPath(assemblyPath);

        return null;
    }
}
