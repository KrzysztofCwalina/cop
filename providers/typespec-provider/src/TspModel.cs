// Represents a TypeSpec object model, its properties, and inheritance.

namespace Cop.Providers.TypeSpec;

/// <summary>
/// Represents a TypeSpec object model, its properties, and inheritance.
/// </summary>
public class TspModel
{
    /// <summary>Gets or sets the model's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the model's declared properties.</summary>
    public List<TspProperty> Properties { get; set; } = [];
    /// <summary>Gets or sets the extended model's type expression, if any.</summary>
    public string? BaseModel { get; set; }
    /// <summary>Gets or sets decorators attached to the model.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
    /// <summary>Gets or sets the model's template parameter names.</summary>
    public List<string> TemplateParameters { get; set; } = [];
}
