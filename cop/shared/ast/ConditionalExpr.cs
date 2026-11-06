// A source-model conditional selecting between true and false expressions.

namespace Cop.Lang;

/// <summary>
/// A source-model conditional selecting between true and false expressions.
/// </summary>
public record ConditionalExpr(Expression Condition, Expression TrueExpr, Expression FalseExpr) : Expression;
