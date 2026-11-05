// Multi-branch match expression: discriminant ? pattern1 => result1 | pattern2 => result2
// | _ => default

namespace Cop.Lang;

/// <summary>
/// Multi-branch match expression: discriminant ? pattern1 => result1 | pattern2 => result2 | _ => default
/// </summary>
public record MatchExpr(Expression Discriminant, List<MatchArm> Arms) : Expression;
