// Token categories recognized by the Cop lexical scanner.

using System.Text;

namespace Cop.Lang;

/// <summary>
/// Token categories recognized by the Cop lexical scanner.
/// </summary>
public enum TokenKind
{
    Identifier,
    StringLiteral,
    VerbatimStringLiteral,
    IntLiteral,
    NumberLiteral,
    True,
    False,
    Nic,

    TypeKeyword,
    CollectionKeyword,
    ImportKeyword,
    LetKeyword,
    CommandKeyword,
    ExportKeyword,
    PredicateKeyword,
    FunctionKeyword,
    ForeachKeyword,
    TestKeyword,
    AsyncKeyword,
    RunKeyword,
    FeedKeyword,
    FlagsKeyword,
    EnumKeyword,
    IntrinsicKeyword,
    DoubleColon,
    Colon,
    AndAnd,
    Ampersand,
    Pipe,
    OrOr,
    Not,
    EqualEqual,
    NotEqual,
    Arrow,
    Dot,
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    Equals,
    Comma,
    QuestionMark,
    GreaterThan,
    LessThan,
    GreaterEqual,
    LessEqual,
    Minus,
    Plus,
    Star,
    Slash,
    Percent,
    DocComment,
    Eof
}
