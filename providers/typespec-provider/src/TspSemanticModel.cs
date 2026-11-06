// Represents parsed TypeSpec namespaces, declarations, decorators, and type references.

namespace Cop.Providers.TypeSpec;

/// <summary>
/// Represents a complete TypeSpec compilation unit (one or more files).
/// </summary>
public class TspSpec
{
    /// <summary>Gets or sets the namespaces declared by the input files.</summary>
    public List<TspNamespace> Namespaces { get; set; } = [];
    /// <summary>Gets or sets the object models declared by the input files.</summary>
    public List<TspModel> Models { get; set; } = [];
    /// <summary>Gets or sets operations declared outside interfaces.</summary>
    public List<TspOperation> Operations { get; set; } = [];
    /// <summary>Gets or sets operation interfaces declared by the input files.</summary>
    public List<TspInterface> Interfaces { get; set; } = [];
    /// <summary>Gets or sets the declared enumerations.</summary>
    public List<TspEnum> Enums { get; set; } = [];
    /// <summary>Gets or sets the declared union types.</summary>
    public List<TspUnion> Unions { get; set; } = [];
    /// <summary>Gets or sets the declared scalar types.</summary>
    public List<TspScalar> Scalars { get; set; } = [];
}
