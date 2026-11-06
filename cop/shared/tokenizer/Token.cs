// A lexical token carrying its category, decoded value, and source line.

using System.Text;

namespace Cop.Lang;

/// <summary>
/// A lexical token carrying its category, decoded value, and source line.
/// </summary>
public record Token(TokenKind Kind, string Value, int Line);
