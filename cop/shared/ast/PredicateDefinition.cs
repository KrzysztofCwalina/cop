// Records predicate parameter constraints and the boolean body.

namespace Cop.Lang;

/// <summary>
/// Defines a named boolean test with its input type and source location.
/// </summary>
public record PredicateDefinition(
    string Name,
    string ParameterType,
    string? Constraint,
    Expression Body,
    int Line,
    bool IsExported = false,
    string? NarrowedType = null,
    string? DocComment = null,
    string? PackageName = null);