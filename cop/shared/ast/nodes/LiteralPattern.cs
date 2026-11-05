// Literal pattern: matches a specific value.

namespace Cop.Lang.Ast;

/// <summary>
/// Literal pattern: matches a specific value.
/// </summary>
public record LiteralPattern(object Value, int Line = 0) : Pattern(Line);
