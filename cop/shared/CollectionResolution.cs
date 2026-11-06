// Describes successful, missing, or ambiguous provider-collection lookup.

namespace Cop.Lang;

/// <summary>
/// Result of resolving a bare or namespace-qualified collection name.
/// </summary>
public abstract record CollectionResolution
{
    /// <summary>The collection resolved to a single list of provider items.</summary>
    public sealed record Found(List<object> Items) : CollectionResolution;

    /// <summary>No registered collection matched the requested name.</summary>
    public sealed record NotFound() : CollectionResolution;

    /// <summary>Multiple provider namespaces expose the requested collection.</summary>
    public sealed record Ambiguous(List<string> Namespaces, string CollectionName) : CollectionResolution;
}
