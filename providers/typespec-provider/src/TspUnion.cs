// Represents a TypeSpec union and its alternative types.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents a TypeSpec union and its alternative types.</summary>
public class TspUnion
{
    /// <summary>Gets or sets the union's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the union's alternative declarations.</summary>
    public List<TspUnionVariant> Variants { get; set; } = [];
    /// <summary>Gets or sets decorators attached to the union.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
