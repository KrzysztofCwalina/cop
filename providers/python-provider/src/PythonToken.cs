// Defines Python lexical token categories, text, and source positions.

namespace Cop.Providers.SourceParsers;

/// <summary>Python token kinds produced by <see cref="PythonLexer"/>.</summary>
internal enum TK
{
    Name,       // identifier or keyword (check .Text)
    Number,     // numeric literal
    Str,        // string literal (all forms: triple/single/f/r/b/u and combos)
    Newline,    // logical end-of-line
    Indent,     // virtual INDENT (indentation increased)
    Dedent,     // virtual DEDENT (indentation decreased)
    LParen,     // (
    RParen,     // )
    LBrack,     // [
    RBrack,     // ]
    LBrace,     // {
    RBrace,     // }
    Comma,      // ,
    Colon,      // :
    Semi,       // ;
    Dot,        // .
    At,         // @  (decorator / matmul)
    Arrow,      // ->
    Walrus,     // :=
    Star,       // *
    StarStar,   // **
    Eq,         // =  (assignment)
    Op,         // any other operator / punctuation
    Eof,
}
