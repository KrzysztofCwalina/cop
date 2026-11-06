// Represents a TypeSpec decorator invocation and its parsed arguments.

namespace Cop.Providers.TypeSpec;

/// <summary>
/// Represents a TypeSpec decorator invocation and its parsed arguments.
/// </summary>
public class TspDecorator
{
    /// <summary>Gets or sets the decorator name without the @ prefix.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the decorator argument expressions.</summary>
    public List<string> Arguments { get; set; } = [];
}
