// Represents parameter types, optionality, and passing modes.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A parsed parameter with its type, location, and default value.
/// </summary>
public record ParameterDeclaration(
    string Name,
    TypeReference? Type,
    bool IsVariadic,
    bool IsKwargs,
    bool HasDefaultValue,
    int Line)
{
    /// <summary>
    /// True when the parameter can be omitted by a caller.
    /// </summary>
    public bool IsOptional { get; init; }
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
    /// <summary>
    /// The parsed source spelling of the parameter default, when present.
    /// </summary>
    public string? DefaultValueText { get; init; }
}
