// Expression segment in an interpolated string: {expr}

namespace Cop.Lang.Ast;

/// <summary>
/// Expression segment in an interpolated string: {expr}
/// </summary>
public record ExpressionPart(Expression Expr, string? Format = null) : StringPart;
