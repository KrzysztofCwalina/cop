namespace Cop.Providers.SourceParsers;

internal enum JsTok { Word, Number, Str, Regex, Punct, LineComment, BlockComment, EOF }

internal readonly record struct JsToken(JsTok Kind, string Value, int Line, int Col)
{
    public bool IsIdent => Kind == JsTok.Word;
    public bool IsWord(string w) => Kind == JsTok.Word && Value == w;
    public bool IsPunct(string p) => Kind == JsTok.Punct && Value == p;
    public bool IsStr => Kind == JsTok.Str;
    public bool IsComment => Kind is JsTok.LineComment or JsTok.BlockComment;
    public bool IsEof => Kind == JsTok.EOF;
    public override string ToString() => $"[{Kind} {Value} {Line}:{Col}]";
}

/// <summary>
/// Real lexer for JavaScript/TypeScript. Handles single/double-quoted strings, template literals
/// with ${} interpolation, // and /* */ comments, regex literals (vs division), numeric literals,
/// and all JS/TS punctuators. Tracks 1-based line and column on every token.
/// </summary>
internal sealed class JsLexer(string src, string path)
{
    public List<string> Errors { get; } = [];

    private int _pos, _line = 1, _col = 1;
    // true when '/' should start a regex (expression-start context)
    private bool _regexOk = true;

    private char Ch(int off = 0) => (_pos + off) < src.Length ? src[_pos + off] : '\0';

    private void Adv()
    {
        if (_pos >= src.Length) return;
        if (src[_pos] == '\n') { _line++; _col = 1; } else _col++;
        _pos++;
    }

    private void Skip(int n) { for (int i = 0; i < n; i++) Adv(); }

    private JsToken Emit(JsTok k, string v, int sl, int sc, bool regexOk)
    {
        _regexOk = regexOk;
        return new JsToken(k, v, sl, sc);
    }

    public List<JsToken> Tokenize()
    {
        var toks = new List<JsToken>();
        while (true) { var t = Next(); toks.Add(t); if (t.IsEof) break; }
        return toks;
    }

    private JsToken Next()
    {
        while (_pos < src.Length && char.IsWhiteSpace(src[_pos])) Adv();
        if (_pos >= src.Length) return new JsToken(JsTok.EOF, "", _line, _col);

        int sl = _line, sc = _col;
        char ch = src[_pos];

        if (ch == '/' && Ch(1) == '/') return LexLineComment(sl, sc);
        if (ch == '/' && Ch(1) == '*') return LexBlockComment(sl, sc);
        if (ch == '\'' || ch == '"') return LexString(ch, sl, sc);
        if (ch == '`') return LexTemplate(sl, sc);
        if (ch == '/' && _regexOk) return LexRegexOrDiv(sl, sc);
        if (char.IsDigit(ch) || (ch == '.' && char.IsDigit(Ch(1)))) return LexNumber(sl, sc);
        if (ch == '_' || ch == '$' || char.IsLetter(ch)) return LexWord(sl, sc);
        if (ch == '@') { Adv(); return Emit(JsTok.Punct, "@", sl, sc, regexOk: true); }
        return LexPunct(sl, sc);
    }

    private JsToken LexLineComment(int sl, int sc)
    {
        int s = _pos;
        while (_pos < src.Length && src[_pos] != '\n') Adv();
        // Line comments do not change regex-allowed state
        return Emit(JsTok.LineComment, src[s.._pos], sl, sc, _regexOk);
    }

    private JsToken LexBlockComment(int sl, int sc)
    {
        int s = _pos;
        Skip(2);
        while (_pos < src.Length - 1 && !(src[_pos] == '*' && src[_pos + 1] == '/')) Adv();
        if (_pos < src.Length - 1) Skip(2);
        else
        {
            Errors.Add($"{path}({sl},{sc}): error: Unterminated block comment");
            while (_pos < src.Length) Adv();
        }
        // Block comments do not change regex-allowed state
        return Emit(JsTok.BlockComment, src[s.._pos], sl, sc, _regexOk);
    }

    private JsToken LexString(char q, int sl, int sc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(q); Adv();
        while (_pos < src.Length)
        {
            char c = src[_pos];
            if (c == '\\') { sb.Append(c); Adv(); if (_pos < src.Length) { sb.Append(src[_pos]); Adv(); } continue; }
            if (c == '\n') break; // Unterminated
            sb.Append(c); Adv();
            if (c == q) return Emit(JsTok.Str, sb.ToString(), sl, sc, regexOk: false);
        }
        Errors.Add($"{path}({sl},{sc}): error: Unterminated string literal");
        return Emit(JsTok.Str, sb.ToString(), sl, sc, regexOk: false);
    }

    private JsToken LexTemplate(int sl, int sc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('`'); Adv();
        while (_pos < src.Length)
        {
            char c = src[_pos];
            if (c == '\\') { sb.Append(c); Adv(); if (_pos < src.Length) { sb.Append(src[_pos]); Adv(); } continue; }
            if (c == '$' && Ch(1) == '{')
            {
                sb.Append("${"); Skip(2);
                int d = 1;
                while (_pos < src.Length && d > 0)
                {
                    // Nested templates inside ${} are handled by counting braces
                    char ec = src[_pos];
                    sb.Append(ec);
                    if (ec == '{') d++;
                    else if (ec == '}') d--;
                    Adv();
                }
                continue;
            }
            if (c == '`') { sb.Append(c); Adv(); return Emit(JsTok.Str, sb.ToString(), sl, sc, regexOk: false); }
            sb.Append(c); Adv();
        }
        Errors.Add($"{path}({sl},{sc}): error: Unterminated template literal");
        return Emit(JsTok.Str, sb.ToString(), sl, sc, regexOk: false);
    }

    private JsToken LexRegexOrDiv(int sl, int sc)
    {
        // Try to lex as regex; fall back to division '/' if newline reached before closing /
        var sb = new System.Text.StringBuilder();
        sb.Append('/'); Adv();
        bool inClass = false;
        while (_pos < src.Length)
        {
            char c = src[_pos];
            if (c == '\\') { sb.Append(c); Adv(); if (_pos < src.Length) { sb.Append(src[_pos]); Adv(); } continue; }
            if (c == '[') { inClass = true; sb.Append(c); Adv(); continue; }
            if (c == ']') { inClass = false; sb.Append(c); Adv(); continue; }
            if (c == '/' && !inClass) { sb.Append(c); Adv(); break; }
            if (c == '\n') return Emit(JsTok.Punct, "/", sl, sc, regexOk: false); // Division
            sb.Append(c); Adv();
        }
        // Read flags: /gi etc.
        while (_pos < src.Length && (char.IsLetter(src[_pos]) || src[_pos] == '_')) { sb.Append(src[_pos]); Adv(); }
        return Emit(JsTok.Regex, sb.ToString(), sl, sc, regexOk: false);
    }

    private JsToken LexNumber(int sl, int sc)
    {
        int s = _pos;
        if (src[_pos] == '0' && _pos + 1 < src.Length && "xXbBoO".Contains(src[_pos + 1]))
        {
            Skip(2);
            while (_pos < src.Length && (char.IsLetterOrDigit(src[_pos]) || src[_pos] == '_')) Adv();
        }
        else
        {
            while (_pos < src.Length && (char.IsDigit(src[_pos]) || src[_pos] == '_')) Adv();
            if (_pos < src.Length && src[_pos] == '.')
            {
                Adv();
                while (_pos < src.Length && char.IsDigit(src[_pos])) Adv();
            }
            if (_pos < src.Length && (src[_pos] == 'e' || src[_pos] == 'E'))
            {
                Adv();
                if (_pos < src.Length && (src[_pos] == '+' || src[_pos] == '-')) Adv();
                while (_pos < src.Length && char.IsDigit(src[_pos])) Adv();
            }
            if (_pos < src.Length && src[_pos] == 'n') Adv(); // BigInt
        }
        return Emit(JsTok.Number, src[s.._pos], sl, sc, regexOk: false);
    }

    private JsToken LexWord(int sl, int sc)
    {
        int s = _pos;
        while (_pos < src.Length && (char.IsLetterOrDigit(src[_pos]) || src[_pos] == '_' || src[_pos] == '$')) Adv();
        string w = src[s.._pos];
        // These keywords put the lexer in a regex-allowed context
        bool ro = w is "return" or "throw" or "typeof" or "instanceof" or "in" or "of"
            or "delete" or "void" or "new" or "yield" or "case" or "do" or "else"
            or "await" or "extends" or "if" or "while";
        return Emit(JsTok.Word, w, sl, sc, ro);
    }

    private static readonly string[] s_multiChar =
    [
        "...", "===", "!==", "=>", "**=", "&&=", "||=", "??=",
        "**", "&&", "||", "??", "?.", "++", "--",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
        "<<", ">>>", ">>", "<=", ">=", "==", "!="
    ];

    private JsToken LexPunct(int sl, int sc)
    {
        foreach (var mp in s_multiChar)
        {
            if (_pos + mp.Length <= src.Length && src.AsSpan(_pos, mp.Length).SequenceEqual(mp))
            {
                Skip(mp.Length);
                return Emit(JsTok.Punct, mp, sl, sc, RegexAfterPunct(mp));
            }
        }
        char c = src[_pos]; Adv();
        return Emit(JsTok.Punct, c.ToString(), sl, sc, RegexAfterPunct(c.ToString()));
    }

    // Returns true when the next token after this punct can be a regex literal
    private static bool RegexAfterPunct(string p) => p is
        "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^="
        or "**=" or "&&=" or "||=" or "??="
        or "==" or "!=" or "===" or "!=="
        or "<" or ">" or "<=" or ">="
        or "&&" or "||" or "??" or "!"
        or "+" or "-" or "*" or "%" or "**"
        or "&" or "|" or "^" or "~"
        or "(" or "[" or "{" or "}" or ";" or ":" or "," or "?" or "=>";
}
