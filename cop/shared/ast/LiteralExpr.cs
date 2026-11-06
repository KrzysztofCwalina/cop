// A constant value embedded in a source-model expression.

namespace Cop.Lang;

/// <summary>
/// A constant value embedded in a source-model expression.
/// </summary>
public record LiteralExpr(object Value) : Expression;
