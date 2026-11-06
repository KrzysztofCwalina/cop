// Represents a TypeSpec operation signature and its enclosing scope.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents a TypeSpec operation signature and its enclosing scope.</summary>
public class TspOperation
{
    /// <summary>Gets or sets the operation's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the enclosing interface name, if any.</summary>
    public string? Interface { get; set; }
    /// <summary>Gets or sets the operation's parameter declarations.</summary>
    public List<TspProperty> Parameters { get; set; } = [];
    /// <summary>Gets or sets the operation's return type expression.</summary>
    public string ReturnType { get; set; } = "void";
    /// <summary>Gets or sets decorators attached to the operation.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
