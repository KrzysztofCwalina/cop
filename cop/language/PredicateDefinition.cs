namespace Cop.Lang;

public record PredicateDefinition(
    string Name,
    string ParameterType,
    string? Constraint,
    Expression Body,
    int Line,
    bool IsExported = false,
    string? NarrowedType = null);