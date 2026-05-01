namespace Cop.Lang;

/// <summary>
/// A lazy proxy object returned by the built-in Code() function.
/// Holds a list of provider names and an optional path. When a property
/// (e.g., .Types, .Methods) is accessed, queries all listed providers
/// and unions the results.
/// </summary>
public class CodeProxy
{
    public string[] Providers { get; }
    public string? Path { get; }

    public CodeProxy(string[] providers, string? path = null)
    {
        Providers = providers;
        Path = path;
    }

    /// <summary>
    /// Queries a named collection from all bound providers and unions results.
    /// When Path is null, reads from pre-loaded collections in the TypeRegistry.
    /// When Path is set, queries providers on-demand via the query service.
    /// </summary>
    public List<object> GetCollection(string collectionName, TypeRegistry typeRegistry, IProviderQueryService? queryService)
    {
        var results = new List<object>();

        foreach (var provider in Providers)
        {
            if (Path is not null && queryService is not null)
            {
                var items = queryService.Query(provider, collectionName, Path);
                results.AddRange(items);
            }
            else
            {
                // Read from pre-loaded namespace collections
                var qualified = $"{provider}.{collectionName}";
                var items = typeRegistry.GetGlobalCollectionItems(qualified);
                if (items is not null)
                    results.AddRange(items);
            }
        }

        return results;
    }

    public override string ToString() => Path is null
        ? $"Code([{string.Join(", ", Providers)}])"
        : $"Code([{string.Join(", ", Providers)}], '{Path}')";
}
