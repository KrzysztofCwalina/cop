using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
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
    private const string TrustFileName = "trusted-providers.json";

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

        // Check trust
        if (!IsProviderTrusted(metadata.Name))
        {
            errors.Add($"Provider package '{metadata.Name}' is not trusted. Run 'cop trust {metadata.Name}' to allow loading.");
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

    /// <summary>
    /// Checks if a provider package is in the trust list.
    /// </summary>
    public static bool IsProviderTrusted(string packageName)
    {
        var trustFile = GetTrustFilePath();
        if (!File.Exists(trustFile))
            return false;

        try
        {
            var json = File.ReadAllText(trustFile);
            var trusted = JsonSerializer.Deserialize<List<string>>(json);
            return trusted?.Contains(packageName, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds a package to the trust list.
    /// </summary>
    public static void TrustProvider(string packageName)
    {
        var trustFile = GetTrustFilePath();
        List<string> trusted = [];

        if (File.Exists(trustFile))
        {
            try
            {
                var json = File.ReadAllText(trustFile);
                trusted = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch
            {
                trusted = [];
            }
        }

        if (!trusted.Contains(packageName, StringComparer.OrdinalIgnoreCase))
        {
            trusted.Add(packageName);
            Directory.CreateDirectory(Path.GetDirectoryName(trustFile)!);
            File.WriteAllText(trustFile, JsonSerializer.Serialize(trusted, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Removes a package from the trust list.
    /// </summary>
    public static void UntrustProvider(string packageName)
    {
        var trustFile = GetTrustFilePath();
        if (!File.Exists(trustFile))
            return;

        try
        {
            var json = File.ReadAllText(trustFile);
            var trusted = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            trusted.RemoveAll(p => string.Equals(p, packageName, StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(trustFile, JsonSerializer.Serialize(trusted, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore errors
        }
    }

    private static string GetTrustFilePath()
    {
        var copDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop");
        return Path.Combine(copDir, TrustFileName);
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
