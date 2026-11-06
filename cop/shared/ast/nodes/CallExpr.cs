// Represents a function or method invocation in the executable syntax tree.

namespace Cop.Lang.Ast;

/// <summary>
/// Function/method call: callee(args) or obj.method(args)
/// </summary>
public record CallExpr(Expression Callee, List<Expression> Args, int Line = 0) : Expression(Line);
