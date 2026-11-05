// Records the element expressions of a list literal.

namespace Cop.Lang.Ast;

/// <summary>
/// List literal: [a, b, c]
/// </summary>
public record ListExpr(List<Expression> Elements, int Line = 0) : Expression(Line);
