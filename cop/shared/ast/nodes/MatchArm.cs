// A single arm in a match expression.

namespace Cop.Lang.Ast;

/// <summary>
/// A single arm in a match expression.
/// </summary>
public record MatchArm(Pattern Pat, Expression Body, int Line = 0) : AstNode(Line);
