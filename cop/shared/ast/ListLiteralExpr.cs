// An ordered list of element expressions in the public source model.

namespace Cop.Lang;

/// <summary>
/// An ordered list of element expressions in the public source model.
/// </summary>
public record ListLiteralExpr(List<Expression> Elements) : Expression;
