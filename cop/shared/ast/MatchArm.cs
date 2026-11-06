// A single arm in a match expression.

namespace Cop.Lang;

/// <summary>
/// A single arm in a match expression. Pattern is null for the wildcard (_) arm.
/// </summary>
public record MatchArm(Expression? Pattern, Expression Result);
