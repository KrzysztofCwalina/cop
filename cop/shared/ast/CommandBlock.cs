// Represents executable command blocks and their statements.

namespace Cop.Lang;

/// <summary>
/// Describes a command's query source, output expression, and execution options.
/// </summary>
public record CommandBlock(
    string Name,
    string MessageTemplate,
    string? Collection,
    List<Expression> Filters,
    int Line,
    string? DocComment = null,
    bool IsCommand = false,
    bool IsExported = false,
    bool IsTest = false,
    string? ActionName = null,
    string? OutputPath = null,
    Expression? Guard = null,
    string? CommandRef = null,
    Expression? Exclusions = null,
    List<string>? Parameters = null,
    Expression? OutputExpression = null,
    SinkTarget? Sink = null,
    string? PathOverride = null,
    bool IsAsync = false,
    bool IsIntrinsic = false);
