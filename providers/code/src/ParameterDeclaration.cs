namespace Cop.Providers.SourceModel;

public record ParameterDeclaration(
    string Name,
    TypeReference? Type,
    bool IsVariadic,
    bool IsKwargs,
    bool HasDefaultValue,
    int Line)
{
    public bool IsOptional { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public string? DefaultValueText { get; init; }
}
