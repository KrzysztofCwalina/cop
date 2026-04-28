namespace Cop.Lang;

/// <summary>
/// Thrown when a bare collection name resolves to multiple provider namespaces.
/// </summary>
public class AmbiguousCollectionException : Exception
{
    public AmbiguousCollectionException(string message) : base(message) { }
}
