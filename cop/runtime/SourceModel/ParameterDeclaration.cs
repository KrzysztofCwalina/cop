namespace Cop.Providers.SourceModel;

public record ParameterDeclaration(
    string Name,
    TypeReference? Type,
    bool IsVariadic,
    bool IsKwargs,
    bool HasDefaultValue,
    int Line)
{
    public string? DefaultValueText { get; init; }
}
