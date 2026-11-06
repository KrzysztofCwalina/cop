// Wildcard pattern: _ (matches anything).

namespace Cop.Lang.Ast;

/// <summary>
/// Wildcard pattern: _ (matches anything).
/// </summary>
public record WildcardPattern(int Line = 0) : Pattern(Line);
