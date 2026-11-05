// Represents a binary operator applied to two syntax expressions.

namespace Cop.Lang.Ast;

/// <summary>
/// Binary operation: left op right
/// </summary>
public record BinaryExpr(Expression Left, BinaryOp Op, Expression Right, int Line = 0) : Expression(Line);
