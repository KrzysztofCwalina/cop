// Represents indexed access on an executable syntax expression.

namespace Cop.Lang.Ast;

/// <summary>
/// Index access: obj[index]
/// </summary>
public record IndexExpr(Expression Object, Expression Index, int Line = 0) : Expression(Line);
