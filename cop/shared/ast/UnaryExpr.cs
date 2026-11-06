// A source-model operation applying a named operator to one expression.

namespace Cop.Lang;

/// <summary>
/// A source-model operation applying a named operator to one expression.
/// </summary>
public record UnaryExpr(string Operator, Expression Operand) : Expression;
