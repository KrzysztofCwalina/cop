namespace Cop.Lang;

public record FunctionParameter(string Name, string TypeName);

/// <summary>
/// A function definition that transforms items from one type to another.
/// Applied in filter chains like predicates, but produces a new typed object
/// instead of a boolean result.
/// </summary>
public record FunctionDefinition(
    string Name,
    string InputType,
    string ReturnType,
    List<FunctionParameter> Parameters,
    Dictionary<string, Expression> FieldMappings,
    int Line,
    bool IsExported = false,
    Expression? BodyExpression = null);
