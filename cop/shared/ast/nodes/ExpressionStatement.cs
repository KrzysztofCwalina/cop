// Expression used as a statement (e.g., a function call with side effects).

namespace Cop.Lang.Ast;

/// <summary>
/// Expression used as a statement (e.g., a function call with side effects).
/// </summary>
public record ExpressionStatement(Expression Expr, int Line = 0) : Statement(Line);
