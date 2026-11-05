// Records a discriminant and ordered pattern-matching arms.

namespace Cop.Lang.Ast;

/// <summary>
/// Match expression: expr ? pattern1 => result1 | pattern2 => result2
/// </summary>
public record MatchExpr(Expression Discriminant, List<MatchArm> Arms, int Line = 0) : Expression(Line);
