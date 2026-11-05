// Represents a function implemented by one syntax expression.

namespace Cop.Lang.Ast;

/// <summary>
/// Function body is a single expression: fn add(a, b) = a + b
/// </summary>
public record ExpressionBody(Expression Expr) : FunctionBody;
