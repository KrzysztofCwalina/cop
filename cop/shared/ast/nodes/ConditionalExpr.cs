// Records condition, true-branch, and false-branch syntax expressions.

namespace Cop.Lang.Ast;

/// <summary>
/// Ternary conditional: cond ? then : else
/// </summary>
public record ConditionalExpr(Expression Condition, Expression Then, Expression Else, int Line = 0) : Expression(Line);
