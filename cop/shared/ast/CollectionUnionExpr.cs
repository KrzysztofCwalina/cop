// A source-model expression combining multiple collection expressions.

namespace Cop.Lang;

/// <summary>
/// A source-model expression combining multiple collection expressions.
/// </summary>
public record CollectionUnionExpr(List<Expression> Elements) : Expression;
