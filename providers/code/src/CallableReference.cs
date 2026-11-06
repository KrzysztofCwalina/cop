// Describes the callable declaration selected by compiler binding.

namespace Cop.Providers.SourceModel;

/// <summary>A compiler-selected callable declaration, not a runtime dispatch target.</summary>
public record CallableReference(string Name, string Signature)
{
    /// <summary>
    /// The type that declares the referenced callable.
    /// </summary>
    public string? DeclaringType { get; init; }
    /// <summary>
    /// The namespace that contains the declaration.
    /// </summary>
    public string? Namespace { get; init; }
    /// <summary>
    /// The parsed or bound return type, when known.
    /// </summary>
    public TypeReference? ReturnType { get; init; }
    /// <summary>
    /// The parameters in declaration order.
    /// </summary>
    public List<TypeReference> Parameters { get; init; } = [];
    /// <summary>
    /// The source path of the referenced declaration, when available.
    /// </summary>
    public string? SourceFile { get; init; }
    /// <summary>
    /// The one-based source line where this element begins.
    /// </summary>
    public int? Line { get; init; }
    /// <summary>
    /// The one-based source column, or null when the parser cannot provide it.
    /// </summary>
    public int? Column { get; init; }
    /// <summary>
    /// The one-based ending source line, when available.
    /// </summary>
    public int? EndLine { get; init; }
    /// <summary>
    /// The one-based exclusive ending source column, when available.
    /// </summary>
    public int? EndColumn { get; init; }
}
