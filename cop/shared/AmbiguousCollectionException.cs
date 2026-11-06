// Reports collection names that cannot be resolved to a single provider namespace.

namespace Cop.Lang;

/// <summary>
/// Thrown when a bare collection name resolves to multiple provider namespaces.
/// </summary>
public class AmbiguousCollectionException : Exception
{
    /// <summary>Reports an ambiguous collection without additional context.</summary>
    public AmbiguousCollectionException() { }

    /// <summary>Reports why a collection name could not be resolved uniquely.</summary>
    public AmbiguousCollectionException(string message) : base(message) { }

    /// <summary>Reports an ambiguity while retaining its underlying failure.</summary>
    public AmbiguousCollectionException(string message, Exception innerException)
        : base(message, innerException) { }
}
