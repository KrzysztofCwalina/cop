// A named member read from the result of another source-model expression.

namespace Cop.Lang;

/// <summary>
/// A named member read from the result of another source-model expression.
/// </summary>
public record MemberAccessExpr(Expression Target, string Member) : Expression;
