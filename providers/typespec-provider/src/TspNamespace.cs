// Represents a TypeSpec namespace and its attached decorators.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents a TypeSpec namespace and its attached decorators.</summary>
public class TspNamespace
{
    /// <summary>Gets or sets the namespace's local name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the namespace name including its enclosing namespaces.
    /// </summary>
    public string FullName { get; set; } = string.Empty;
    /// <summary>Gets or sets decorators attached to the namespace.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
