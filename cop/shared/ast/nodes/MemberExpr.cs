// Represents member access on an executable syntax expression.

namespace Cop.Lang.Ast;

/// <summary>
/// Member access: obj.member
/// </summary>
public record MemberExpr(Expression Object, string Member, int Line = 0) : Expression(Line);
