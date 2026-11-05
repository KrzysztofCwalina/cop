// Unified call expression.

namespace Cop.Lang;

/// <summary>
/// Unified call expression. Target is null for standalone calls (e.g., Load('path'), FAIL('msg')),
/// non-null for postfix calls (e.g., x:foo(), x.bar()). Negated is only valid for predicate calls.
/// </summary>
public record CallExpr(Expression? Target, string Name, List<Expression> Args, bool Negated = false) : Expression;
