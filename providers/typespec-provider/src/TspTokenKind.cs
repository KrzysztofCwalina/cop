// Identifies the lexical categories recognized by the TypeSpec parser.

using System.Text;

namespace Cop.Providers.TypeSpec;

/// <summary>
/// Identifies the lexical categories recognized by the TypeSpec parser.
/// </summary>
public enum TspTokenKind
{
    // Literals
    Identifier,
    StringLiteral,
    NumericLiteral,
    TripleQuoteString,

    // Keywords
    Model,
    Op,
    Interface,
    Enum,
    Union,
    Scalar,
    Namespace,
    Import,
    Using,
    Extends,
    Is,
    If,
    Else,
    Alias,
    Extern,
    Dec,
    Fn,
    Const,
    True,
    False,
    Void,
    Never,
    Unknown,

    // Punctuation
    OpenBrace,
    CloseBrace,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Comma,
    Colon,
    Dot,
    DotDotDot,
    At,
    Hash,
    QuestionMark,
    Equals,
    Pipe,
    Ampersand,
    Arrow,        // =>

    // Special
    EndOfFile,
    Unknown_Token,
}
