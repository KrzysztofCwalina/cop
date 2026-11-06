// Represents a TypeSpec enumeration and its members.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents a TypeSpec enumeration and its members.</summary>
public class TspEnum
{
    /// <summary>Gets or sets the enumeration's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the enumeration's member declarations.</summary>
    public List<TspEnumMember> Members { get; set; } = [];
    /// <summary>Gets or sets decorators attached to the enumeration.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
