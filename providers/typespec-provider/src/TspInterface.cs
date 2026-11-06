// Represents a TypeSpec interface containing operation declarations.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents a TypeSpec interface containing operation declarations.</summary>
public class TspInterface
{
    /// <summary>Gets or sets the interface's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets operations declared by the interface.</summary>
    public List<TspOperation> Operations { get; set; } = [];
    /// <summary>Gets or sets decorators attached to the interface.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
    /// <summary>Gets or sets the extended interface type expressions.</summary>
    public List<string> Extends { get; set; } = [];
    /// <summary>Gets or sets the interface's template parameter names.</summary>
    public List<string> TemplateParameters { get; set; } = [];
}
