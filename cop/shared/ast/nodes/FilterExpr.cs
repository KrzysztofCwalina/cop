// Records predicate filtering over a collection expression.

namespace Cop.Lang.Ast;

/// <summary>
/// Filter application: collection:predicate (syntactic sugar for filtering)
/// </summary>
public record FilterExpr(Expression Collection, Expression Predicate, bool Negated = false, int Line = 0) : Expression(Line);
