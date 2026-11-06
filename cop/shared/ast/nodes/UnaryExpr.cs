// Represents a unary operator applied to a syntax expression.

namespace Cop.Lang.Ast;

/// <summary>
/// Unary operation: !x, -x
/// </summary>
public record UnaryExpr(UnaryOp Op, Expression Operand, int Line = 0) : Expression(Line);
