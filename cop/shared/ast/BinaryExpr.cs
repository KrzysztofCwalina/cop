// A source-model operation combining two expressions with a named operator.

namespace Cop.Lang;

/// <summary>
/// A source-model operation combining two expressions with a named operator.
/// </summary>
public record BinaryExpr(Expression Left, string Operator, Expression Right) : Expression;
