// Represents a constant value in the executable syntax tree.

namespace Cop.Lang.Ast;

/// <summary>
/// Literal value: 42, 'hello', true, null
/// </summary>
public record LiteralExpr(object? Value, int Line = 0) : Expression(Line);
