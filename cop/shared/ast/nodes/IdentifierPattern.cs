// Identifier pattern: binds matched value to a name.

namespace Cop.Lang.Ast;

/// <summary>
/// Identifier pattern: binds matched value to a name.
/// </summary>
public record IdentifierPattern(string Name, int Line = 0) : Pattern(Line);
