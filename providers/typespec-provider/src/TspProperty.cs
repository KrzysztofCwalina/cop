// Represents a model property or operation parameter in TypeSpec source.

namespace Cop.Providers.TypeSpec;

/// <summary>
/// Represents a model property or operation parameter in TypeSpec source.
/// </summary>
public class TspProperty
{
    /// <summary>Gets or sets the property or parameter name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the declared type expression.</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Gets or sets whether the declaration has an optional marker.</summary>
    public bool IsOptional { get; set; }
    /// <summary>Gets or sets the default value expression, if present.</summary>
    public string? Default { get; set; }
    /// <summary>Gets or sets decorators attached to the declaration.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
