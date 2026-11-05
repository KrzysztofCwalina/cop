// Literal text segment in an interpolated string.

namespace Cop.Lang.Ast;

/// <summary>
/// Literal text segment in an interpolated string.
/// </summary>
public record TextPart(string Text) : StringPart;
