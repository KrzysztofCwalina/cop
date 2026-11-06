// Tokenizes JavaScript and TypeScript while retaining comment and source positions.

using System.Text;

namespace Cop.Providers.SourceParsers;

internal readonly record struct JsToken(JsTok Kind, string Value, int Line, int Col)
{
    /// <summary>Gets whether this token is a word or identifier.</summary>
    public bool IsIdent => Kind == JsTok.Word;
    /// <summary>Tests whether this token is the specified word.</summary>
    public bool IsWord(string w) => Kind == JsTok.Word && Value == w;
    /// <summary>Tests whether this token is the specified punctuation.</summary>
    public bool IsPunct(string p) => Kind == JsTok.Punct && Value == p;
    /// <summary>Gets whether this token is a string literal.</summary>
    public bool IsStr => Kind == JsTok.Str;
    /// <summary>Gets whether this token is a line or block comment.</summary>
    public bool IsComment => Kind is JsTok.LineComment or JsTok.BlockComment;
    /// <summary>Gets whether this token marks the end of the source.</summary>
    public bool IsEof => Kind == JsTok.EOF;
    /// <inheritdoc />
    public override string ToString() => $"[{Kind} {Value} {Line}:{Col}]";
}
