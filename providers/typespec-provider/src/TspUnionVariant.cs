// Represents one named alternative of a TypeSpec union.

namespace Cop.Providers.TypeSpec;

/// <summary>Represents one named alternative of a TypeSpec union.</summary>
public class TspUnionVariant
{
    /// <summary>Gets or sets the alternative's declared name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the alternative's type expression.</summary>
    public string Type { get; set; } = string.Empty;
}
