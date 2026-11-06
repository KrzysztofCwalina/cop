// Represents a TypeSpec scalar and its optional base scalar.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents a TypeSpec scalar and its optional base scalar.</summary>
public class TspScalar
{
    /// <summary>Gets or sets the scalar's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the extended scalar's type expression, if any.</summary>
    public string? BaseScalar { get; set; }
    /// <summary>Gets or sets decorators attached to the scalar.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
