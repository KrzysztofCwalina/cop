namespace Cop.Providers.SourceParsers;

/// <summary>
/// Full Python 3 lexer. Produces a flat list of <see cref="Tok"/> tokens including virtual
/// INDENT/DEDENT tokens for Python's indentation-based block structure.  Correctly handles:
/// triple-quoted strings (all prefix combinations: f/r/b/u and their combos), line comments,
/// numeric literals (hex/oct/binary/float/complex/underscore-separator), operators, and both
/// implicit continuation (inside brackets) and explicit continuation (backslash).
/// </summary>
internal sealed class PythonLexer
{
    private readonly string _src;
    private readonly string _filePath;
    private int _pos;
    private int _line;
    private int _col;
    private int _bracketDepth;          // () [] {} nesting — suppresses INDENT/DEDENT/NEWLINE
    private readonly Stack<int> _indentStack = new([0]);
    private readonly Queue<Tok> _pending = new();
    private bool _atBol;                // at beginning of a physical line

    /// <summary>1-based line numbers whose first non-whitespace character is '#'.</summary>
    public HashSet<int> CommentLines { get; } = [];

    /// <summary>Lex errors in the standard parse-error format.</summary>
    public List<string> LexErrors { get; } = [];

    public PythonLexer(string src, string filePath = "")
    {
        _src = src;
        _filePath = filePath;
        _line = 1;
        _col = 1;
        _atBol = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    public List<Tok> Tokenize()
    {
        var toks = new List<Tok>();
        while (true)
        {
            var t = Next();
            toks.Add(t);
            if (t.Kind == TK.Eof) break;
        }
        return toks;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main token dispatch
    // ─────────────────────────────────────────────────────────────────────────

    private Tok Next()
    {
        if (_pending.Count > 0) return _pending.Dequeue();

    Top:
        // ── Handle beginning of physical line ──────────────────────────────
        if (_atBol)
        {
            _atBol = false;
            int bolLine = _line;
            int indent = ScanLeadingWhitespace();

            if (AtEnd()) goto Eof;

            char fc = Cur();

            // Blank line — skip without emitting INDENT/DEDENT
            if (fc == '\n' || fc == '\r')
            {
                AdvanceNewline();
                _atBol = true;
                goto Top;
            }

            // Comment-only line
            if (fc == '#')
            {
                CommentLines.Add(_line);
                SkipRestOfLine();
                if (!AtEnd()) AdvanceNewline();
                _atBol = true;
                goto Top;
            }

            // Process indentation change (suppressed inside brackets)
            if (_bracketDepth == 0)
            {
                int top = _indentStack.Peek();
                if (indent > top)
                {
                    _indentStack.Push(indent);
                    return new Tok(TK.Indent, "", bolLine, 1);
                }
                if (indent < top)
                {
                    _indentStack.Pop();
                    // Emit additional DEDENTs for multi-level dedent
                    while (_indentStack.Count > 1 && _indentStack.Peek() > indent)
                    {
                        _indentStack.Pop();
                        _pending.Enqueue(new Tok(TK.Dedent, "", bolLine, 1));
                    }
                    if (_indentStack.Peek() != indent)
                    {
                        LexErrors.Add($"{_filePath}({bolLine},1): error: unindent does not match any outer indentation level");
                        while (_indentStack.Count > 1) _indentStack.Pop();
                        _indentStack.Push(indent); // recover
                    }
                    return new Tok(TK.Dedent, "", bolLine, 1);
                }
                // same level: fall through to token reading
            }
        }

        // ── Skip horizontal whitespace ──────────────────────────────────────
        while (!AtEnd() && (Cur() == ' ' || Cur() == '\t'))
            Advance();

        if (AtEnd()) goto Eof;

        {
            char c = Cur();
            int tl = _line, tc = _col;

            // Logical newline (or implicit continuation inside brackets)
            if (c == '\n' || c == '\r')
            {
                AdvanceNewline();
                _atBol = true;
                if (_bracketDepth > 0) goto Top;   // implicit continuation
                return new Tok(TK.Newline, "\n", tl, tc);
            }

            // Explicit line continuation
            if (c == '\\')
            {
                Advance();
                if (!AtEnd() && (Cur() == '\n' || Cur() == '\r'))
                {
                    AdvanceNewline(); // continuation — next physical line is still same logical line
                    goto Top;        // do NOT set _atBol (no indent processing)
                }
                return new Tok(TK.Op, "\\", tl, tc);
            }

            // Mid-line comment — skip rest of line but do NOT mark as comment line
            if (c == '#')
            {
                SkipRestOfLine();
                goto Top;
            }

            // String literal (checked before identifier to handle r"..." etc.)
            if (TryStringStart(out int pfxLen, out bool isTriple, out char qChar))
                return LexString(tl, tc, pfxLen, isTriple, qChar);

            // Number literal
            if (char.IsDigit(c) || (c == '.' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1])))
                return LexNumber(tl, tc);

            // Identifier / keyword
            if (char.IsLetter(c) || c == '_')
                return LexIdent(tl, tc);

            // Punctuation / operators
            return LexPunct(tl, tc);
        }

    Eof:
        // Flush remaining indent levels then emit EOF
        while (_indentStack.Count > 1)
        {
            _indentStack.Pop();
            _pending.Enqueue(new Tok(TK.Dedent, "", _line, _col));
        }
        _pending.Enqueue(new Tok(TK.Eof, "", _line, _col));
        return _pending.Dequeue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // String lexer — handles all Python 3 string prefix combinations
    // ─────────────────────────────────────────────────────────────────────────

    private bool TryStringStart(out int prefixLen, out bool isTriple, out char quoteChar)
    {
        prefixLen = 0; isTriple = false; quoteChar = '\0';
        int i = _pos;
        // Up to 2 string-prefix chars (r/R/b/B/f/F/u/U and two-char combos like rb, fr, …)
        while (i < _src.Length && i - _pos < 2 && "rRbBfFuU".Contains(_src[i]))
            i++;
        if (i >= _src.Length) return false;
        char q = _src[i];
        if (q != '"' && q != '\'') return false;
        prefixLen = i - _pos;
        quoteChar = q;
        isTriple = i + 2 < _src.Length && _src[i + 1] == q && _src[i + 2] == q;
        return true;
    }

    private Tok LexString(int tl, int tc, int prefixLen, bool isTriple, char q)
    {
        bool isRaw = false, isFStr = false;
        for (int k = 0; k < prefixLen; k++)
        {
            char p = char.ToLower(Cur());
            if (p == 'r') isRaw = true;
            if (p == 'f') isFStr = true;
            Advance();
        }

        if (isTriple)
        {
            Advance(); Advance(); Advance(); // consume opening triple-quote
            while (!AtEnd())
            {
                char c = Cur();
                if (!isRaw && c == '\\')
                {
                    Advance();
                    if (!AtEnd()) { if (Cur() == '\n' || Cur() == '\r') AdvanceNewline(); else Advance(); }
                    continue;
                }
                if (c == q && Peek(1) == q && Peek(2) == q)
                {
                    Advance(); Advance(); Advance();
                    return new Tok(TK.Str, "", tl, tc);
                }
                if (c == '\n' || c == '\r') AdvanceNewline();
                else Advance();
            }
            LexErrors.Add($"{_filePath}({tl},{tc}): error: unterminated triple-quoted string");
            return new Tok(TK.Str, "", tl, tc);
        }

        // Single-line string
        Advance(); // opening quote
        int exprDepth = 0; // f-string expression depth
        while (!AtEnd())
        {
            char c = Cur();
            if (c == '\n' || c == '\r') break; // unterminated
            if (!isRaw && c == '\\')
            {
                Advance();
                if (!AtEnd() && Cur() != '\n' && Cur() != '\r') Advance();
                continue;
            }
            if (isFStr && c == '{')
            {
                if (Peek(1) == '{') { Advance(); Advance(); continue; } // escaped {{
                exprDepth++; Advance(); continue;
            }
            if (isFStr && c == '}' && exprDepth > 0) { exprDepth--; Advance(); continue; }
            if (c == q && exprDepth == 0) { Advance(); return new Tok(TK.Str, "", tl, tc); }
            Advance();
        }
        LexErrors.Add($"{_filePath}({tl},{tc}): error: unterminated string literal");
        return new Tok(TK.Str, "", tl, tc);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Number lexer
    // ─────────────────────────────────────────────────────────────────────────

    private Tok LexNumber(int tl, int tc)
    {
        // Hex / octal / binary literals
        if (!AtEnd() && Cur() == '0' && _pos + 1 < _src.Length)
        {
            char nx = char.ToLower(_src[_pos + 1]);
            if (nx is 'x' or 'o' or 'b')
            {
                Advance(); Advance();
                while (!AtEnd() && (IsHexDigitOrUnderscore(Cur()))) Advance();
                if (!AtEnd() && (Cur() is 'j' or 'J')) Advance();
                return new Tok(TK.Number, "", tl, tc);
            }
        }
        // Decimal / float (also handles leading '.')
        while (!AtEnd() && (char.IsDigit(Cur()) || Cur() == '_')) Advance();
        if (!AtEnd() && Cur() == '.' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '.'))
        {
            Advance();
            while (!AtEnd() && (char.IsDigit(Cur()) || Cur() == '_')) Advance();
        }
        if (!AtEnd() && (Cur() is 'e' or 'E'))
        {
            Advance();
            if (!AtEnd() && (Cur() is '+' or '-')) Advance();
            while (!AtEnd() && (char.IsDigit(Cur()) || Cur() == '_')) Advance();
        }
        if (!AtEnd() && (Cur() is 'j' or 'J')) Advance();
        return new Tok(TK.Number, "", tl, tc);
    }

    private static bool IsHexDigitOrUnderscore(char c) =>
        char.IsDigit(c) || c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F') or '_';

    // ─────────────────────────────────────────────────────────────────────────
    // Identifier lexer
    // ─────────────────────────────────────────────────────────────────────────

    private Tok LexIdent(int tl, int tc)
    {
        int start = _pos;
        while (!AtEnd() && (char.IsLetterOrDigit(Cur()) || Cur() == '_'))
            Advance();
        return new Tok(TK.Name, _src[start.._pos], tl, tc);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Punctuation / operator lexer
    // ─────────────────────────────────────────────────────────────────────────

    private Tok LexPunct(int tl, int tc)
    {
        char c = Cur();
        Advance();
        switch (c)
        {
            case '(': _bracketDepth++; return new Tok(TK.LParen,  "(", tl, tc);
            case ')': if (_bracketDepth > 0) _bracketDepth--; return new Tok(TK.RParen, ")", tl, tc);
            case '[': _bracketDepth++; return new Tok(TK.LBrack,  "[", tl, tc);
            case ']': if (_bracketDepth > 0) _bracketDepth--; return new Tok(TK.RBrack, "]", tl, tc);
            case '{': _bracketDepth++; return new Tok(TK.LBrace,  "{", tl, tc);
            case '}': if (_bracketDepth > 0) _bracketDepth--; return new Tok(TK.RBrace, "}", tl, tc);
            case ',': return new Tok(TK.Comma, ",", tl, tc);
            case ';': return new Tok(TK.Semi,  ";", tl, tc);
            case '.':
                if (!AtEnd() && Cur() == '.' && Peek(1) == '.') { Advance(); Advance(); return new Tok(TK.Op, "...", tl, tc); }
                return new Tok(TK.Dot, ".", tl, tc);
            case '@':
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "@=", tl, tc); }
                return new Tok(TK.At, "@", tl, tc);
            case ':':
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Walrus, ":=", tl, tc); }
                return new Tok(TK.Colon, ":", tl, tc);
            case '-':
                if (!AtEnd() && Cur() == '>') { Advance(); return new Tok(TK.Arrow,   "->", tl, tc); }
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op,      "-=", tl, tc); }
                return new Tok(TK.Op, "-", tl, tc);
            case '*':
                if (!AtEnd() && Cur() == '*') { Advance(); if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "**=", tl, tc); } return new Tok(TK.StarStar, "**", tl, tc); }
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op,      "*=", tl, tc); }
                return new Tok(TK.Star, "*", tl, tc);
            case '=':
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "==", tl, tc); }
                return new Tok(TK.Eq, "=", tl, tc);
            case '+': if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "+=",  tl, tc); } return new Tok(TK.Op, "+",  tl, tc);
            case '/':
                if (!AtEnd() && Cur() == '/') { Advance(); if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "//=", tl, tc); } return new Tok(TK.Op, "//", tl, tc); }
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "/=",  tl, tc); }
                return new Tok(TK.Op, "/",  tl, tc);
            case '%': if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "%=",  tl, tc); } return new Tok(TK.Op, "%",  tl, tc);
            case '&': if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "&=",  tl, tc); } return new Tok(TK.Op, "&",  tl, tc);
            case '|': if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "|=",  tl, tc); } return new Tok(TK.Op, "|",  tl, tc);
            case '^': if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "^=",  tl, tc); } return new Tok(TK.Op, "^",  tl, tc);
            case '~': return new Tok(TK.Op, "~", tl, tc);
            case '<':
                if (!AtEnd() && Cur() == '<') { Advance(); if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "<<=", tl, tc); } return new Tok(TK.Op, "<<", tl, tc); }
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "<=",  tl, tc); }
                return new Tok(TK.Op, "<",  tl, tc);
            case '>':
                if (!AtEnd() && Cur() == '>') { Advance(); if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, ">>=", tl, tc); } return new Tok(TK.Op, ">>", tl, tc); }
                if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, ">=",  tl, tc); }
                return new Tok(TK.Op, ">",  tl, tc);
            case '!': if (!AtEnd() && Cur() == '=') { Advance(); return new Tok(TK.Op, "!=",  tl, tc); } return new Tok(TK.Op, "!",  tl, tc);
            default: return new Tok(TK.Op, c.ToString(), tl, tc);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Character-level helpers
    // ─────────────────────────────────────────────────────────────────────────

    private bool AtEnd() => _pos >= _src.Length;
    private char Cur() => _src[_pos];
    private char Peek(int offset = 1) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';

    private void Advance()
    {
        char c = _src[_pos++];
        if (c == '\n') { _line++; _col = 1; }
        else _col++;
    }

    private void AdvanceNewline()
    {
        if (!AtEnd() && Cur() == '\r') { _pos++; _col++; }
        if (!AtEnd() && Cur() == '\n') { _pos++; _line++; _col = 1; }
    }

    private void SkipRestOfLine()
    {
        while (!AtEnd() && Cur() != '\n' && Cur() != '\r')
            Advance();
    }

    private int ScanLeadingWhitespace()
    {
        int indent = 0;
        while (!AtEnd() && (Cur() == ' ' || Cur() == '\t'))
        {
            if (Cur() == '\t') indent = (indent / 8 + 1) * 8;
            else indent++;
            _pos++; _col++;
        }
        return indent;
    }
}
