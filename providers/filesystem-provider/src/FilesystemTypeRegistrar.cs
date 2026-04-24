using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Compatibility shim that delegates to <see cref="FilesystemProvider"/>.
/// Existing code (Engine, tests) can continue calling Register() and Scan()
/// while the actual logic lives in the provider abstraction.
/// </summary>
public static class FilesystemTypeRegistrar
{
    private static readonly FilesystemProvider _provider = new();

    /// <summary>
    /// Registers type descriptors from the filesystem provider schema.
    /// Accessors are auto-generated from schema (slot-based reads via DataObjectView).
    /// </summary>
    public static void Register(TypeRegistry registry)
    {
        var schema = ProviderSchema.FromJson(_provider.GetSchema());
        registry.RegisterProviderSchema(schema);
        registry.RegisterDataTableAccessors(schema);
    }

    /// <summary>
    /// Scans the filesystem and registers Folders/DiskFiles as global collections
    /// backed by packed DataObject[] records with a shared string heap.
    /// </summary>
    public static void Scan(TypeRegistry registry, string rootPath)
    {
        var query = new ProviderQuery { RootPath = rootPath };
        var tables = _provider.QueryData(query);

        foreach (var (collName, table) in tables)
        {
            var views = new List<object>(table.Count);
            for (int i = 0; i < table.Count; i++)
                views.Add(new DataObjectView(table, i));
            registry.RegisterGlobalCollection(collName, views);
        }
    }

    /// <summary>
    /// Directories excluded from filesystem scanning.
    /// Exposed for Engine directory pruning.
    /// </summary>
    public static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
        ".nuget", ".dotnet", "TestResults",
        "__pycache__", ".mypy_cache", ".pytest_cache",
        "dist", ".next", ".cache"
    };
}
