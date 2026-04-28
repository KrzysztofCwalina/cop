namespace Cop.Lang;

public record CommandBlock(
    string Name,
    string MessageTemplate,
    string? Collection,
    List<Expression> Filters,
    int Line,
    string? DocComment = null,
    bool IsCommand = false,
    bool IsExported = false,
    string? ActionName = null,
    string? OutputPath = null,
    Expression? Guard = null,
    string? CommandRef = null,
    Expression? Exclusions = null,
    List<string>? Parameters = null);
