// Isolated assembly load context for provider DLLs.

using System.Reflection;
using System.Runtime.Loader;
using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Isolated assembly load context for provider DLLs.
/// Shares the host assembly (cop.exe) with the default context
/// to avoid type identity splits for DataProvider, SourceFile, etc.
/// </summary>
internal class ProviderLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly string HostAssemblyName = typeof(DataProvider).Assembly.GetName().Name!;

    public ProviderLoadContext(string pluginPath) : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the host assembly with the default context so provider types
        // (ObjectProvider, SourceFile, etc.) have the same identity in both contexts
        if (assemblyName.Name == HostAssemblyName)
        {
            return null; // falls back to default context
        }

        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null && File.Exists(assemblyPath))
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        // Fall back to default context (host cop.exe) for assemblies not shipped with the provider
        return null;
    }
}
