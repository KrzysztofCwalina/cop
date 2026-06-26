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

/// <summary>A single token produced by <see cref="PythonLexer"/>.</summary>
internal readonly struct Tok(TK kind, string text, int line, int col)
{
    public readonly TK Kind = kind;
    public readonly string Text = text;
    public readonly int Line = line;   // 1-based
    public readonly int Col = col;     // 1-based

    /// <summary>True when this is a Name token whose text equals <paramref name="kw"/>.</summary>
    public bool Is(string kw) => Kind == TK.Name && Text == kw;

    public override string ToString() => $"{Kind}({Text})@{Line}:{Col}";
}
