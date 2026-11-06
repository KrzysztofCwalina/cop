// Represents a named TypeSpec enumeration value.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents a named TypeSpec enumeration value.</summary>
public class TspEnumMember
{
    /// <summary>Gets or sets the member's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the explicit member value, if present.</summary>
    public string? Value { get; set; }
    /// <summary>Gets or sets decorators attached to the member.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
