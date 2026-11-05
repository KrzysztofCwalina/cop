// A collection reference with a path override: namespace.Collection('path') Parsed from
// dotted member access with a single string argument.

namespace Cop.Lang;

/// <summary>
/// A collection reference with a path override: namespace.Collection('path')
/// Parsed from dotted member access with a single string argument.
/// </summary>
public record PathScopedExpr(Expression Inner, string Path) : Expression;
