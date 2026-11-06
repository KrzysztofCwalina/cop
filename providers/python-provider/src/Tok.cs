// A single token produced by <see cref="PythonLexer"/>.

namespace Cop.Providers.SourceParsers;

/// <summary>A single token produced by <see cref="PythonLexer"/>.</summary>
internal readonly struct Tok(TK kind, string text, int line, int col)
{
    /// <summary>Identifies the token's lexical category.</summary>
    public readonly TK Kind = kind;
    /// <summary>Contains the token's source spelling.</summary>
    public readonly string Text = text;
    /// <summary>Identifies the token's one-based source line.</summary>
    public readonly int Line = line;   // 1-based
    /// <summary>Identifies the token's one-based source column.</summary>
    public readonly int Col = col;     // 1-based

    /// <summary>True when this is a Name token whose text equals <paramref name="kw"/>.</summary>
    public bool Is(string kw) => Kind == TK.Name && Text == kw;

    /// <inheritdoc />
    public override string ToString() => $"{Kind}({Text})@{Line}:{Col}";
}
