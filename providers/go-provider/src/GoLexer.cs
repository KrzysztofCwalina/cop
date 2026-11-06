// Tokenizes Go source and projects declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal class GoLexer(string source)
{
    private int _pos;
    private int _line = 1;
    private int _lineStart = 0; // character offset of the start of the current line

    private readonly List<(int Line, int Col, string Message)> _lexErrors = [];

    /// <summary>Lex-phase errors (unterminated strings / block comments).</summary>
    public IReadOnlyList<(int Line, int Col, string Message)> LexErrors => _lexErrors;

    private static readonly HashSet<string> s_keywords =
    [
        "break", "case", "chan", "const", "continue", "default", "defer",
        "else", "fallthrough", "for", "func", "go", "goto", "if", "import",
        "interface", "map", "package", "range", "return", "select", "struct",
        "switch", "type", "var"
    ];

    private int ColAt(int pos) => pos - _lineStart + 1;

    /// <summary>Reads Go tokens with their source positions.</summary>
    public List<GoToken> Tokenize()
    {
        List<GoToken> tokens = new List<GoToken>();
        while (_pos < source.Length)
        {
            SkipWhitespace();
            if (_pos >= source.Length)
            {
                break;
            }

            char c = source[_pos];

            // Comments
            if (c == '/' && _pos + 1 < source.Length)
            {
                if (source[_pos + 1] == '/')
                {
                    tokens.Add(ReadLineComment());
                    continue;
                }
                if (source[_pos + 1] == '*')
                {
                    tokens.Add(ReadBlockComment());
                    continue;
                }
            }

            // String literals
            if (c == '"')
            {
                tokens.Add(ReadString());
                continue;
            }

            // Raw string (backtick) — may span multiple lines
            if (c == '`')
            {
                tokens.Add(ReadRawString());
                continue;
            }

            // Rune literal
            if (c == '\'')
            {
                tokens.Add(ReadRune());
                continue;
            }

            // Numbers
            if (char.IsDigit(c))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_')
            {
                tokens.Add(ReadIdentifierOrKeyword());
                continue;
            }

            // Punctuation
            tokens.Add(ReadPunctuation());
        }

        tokens.Add(new GoToken(GoTokenKind.Eof, string.Empty, _line, ColAt(_pos), _pos, _pos));
        return InsertSemicolons(tokens);
    }

    /// <summary>
    /// Applies Go's automatic semicolon insertion (ASI) rule post-tokenization.
    /// A virtual semicolon is inserted after the last token on a line when that token is
    /// an identifier, literal, break/continue/fallthrough/return, or one of ++, --, ), ], }.
    /// </summary>
    private static List<GoToken> InsertSemicolons(List<GoToken> raw)
    {
        List<GoToken> result = new List<GoToken>(raw.Count + 32);
        for (int i = 0; i < raw.Count; i++)
        {
            GoToken cur = raw[i];
            result.Add(cur);

            // Comments are transparent to ASI — skip them when finding the next real token.
            if (cur.Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment)
            {
                continue;
            }

            if (!NeedsAutoSemicolon(cur))
            {
                continue;
            }

            // Find the next non-comment token to check if it's on a later line.
            int j = i + 1;
            while (j < raw.Count && raw[j].Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment)
            {
                j++;
            }

            bool nextIsLaterLine = j >= raw.Count || raw[j].Line > cur.Line;
            if (nextIsLaterLine)
            {
                result.Add(new GoToken(GoTokenKind.Punctuation, ";", cur.Line, cur.Col + cur.Value.Length, cur.End, cur.End));
            }
        }
        return result;
    }

    private static bool NeedsAutoSemicolon(GoToken t)
    {
        if (t.Kind is GoTokenKind.Identifier or GoTokenKind.StringLiteral or GoTokenKind.NumberLiteral)
        {
            return true;
        }

        if (t.Kind == GoTokenKind.Keyword && t.Value is "break" or "continue" or "fallthrough" or "return")
        {
            return true;
        }

        if (t.Kind == GoTokenKind.Punctuation && t.Value is "++" or "--" or ")" or "]" or "}")
        {
            return true;
        }

        return false;
    }

    private void SkipWhitespace()
    {
        while (_pos < source.Length)
        {
            char c = source[_pos];
            if (c == '\n') { _pos++; _line++; _lineStart = _pos; }
            else if (c is ' ' or '\t' or '\r')
            {
                _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private GoToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        while (_pos < source.Length && source[_pos] != '\n')
        {
            _pos++;
        }

        return new GoToken(GoTokenKind.LineComment, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadBlockComment()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        _pos += 2; // skip /*
        bool closed = false;
        while (_pos < source.Length)
        {
            if (source[_pos] == '*' && _pos + 1 < source.Length && source[_pos + 1] == '/')
            { _pos += 2; closed = true; break; }
            if (source[_pos] == '\n') { _pos++; _line++; _lineStart = _pos; }
            else
            {
                _pos++;
            }
        }
        if (!closed)
        {
            _lexErrors.Add((line, col, "unterminated block comment"));
        }

        return new GoToken(GoTokenKind.BlockComment, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadString()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        _pos++; // skip "
        bool closed = false;
        while (_pos < source.Length)
        {
            char c = source[_pos];
            if (c == '"') { _pos++; closed = true; break; }
            if (c == '\n')
            {
                break; // Go interpreted strings must not span lines
            }

            if (c == '\\') { _pos++; if (_pos < source.Length)
                {
                    _pos++;
                }
            }
            else
            {
                _pos++;
            }
        }
        if (!closed)
        {
            _lexErrors.Add((line, col, "unterminated string literal"));
        }

        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadRawString()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        _pos++; // skip `
        bool closed = false;
        while (_pos < source.Length)
        {
            if (source[_pos] == '`') { _pos++; closed = true; break; }
            if (source[_pos] == '\n') { _pos++; _line++; _lineStart = _pos; }
            else
            {
                _pos++;
            }
        }
        if (!closed)
        {
            _lexErrors.Add((line, col, "unterminated raw string literal"));
        }

        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadRune()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        _pos++; // skip '
        if (_pos < source.Length && source[_pos] == '\\') { _pos++; if (_pos < source.Length)
            {
                _pos++;
            }
        }
        else if (_pos < source.Length)
        {
            _pos++;
        }

        if (_pos < source.Length && source[_pos] == '\'')
        {
            _pos++;
        }

        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '.' or '_' or 'x' or 'X'))
        {
            _pos++;
        }

        return new GoToken(GoTokenKind.NumberLiteral, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadIdentifierOrKeyword()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
        {
            _pos++;
        }

        string value = source[start.._pos];
        GoTokenKind kind = s_keywords.Contains(value) ? GoTokenKind.Keyword : GoTokenKind.Identifier;
        return new GoToken(kind, value, line, col, start, _pos);
    }

    private GoToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        if (_pos + 2 < source.Length)
        {
            string three = source.Substring(_pos, 3);
            if (three is "..." or "<<=" or ">>=")
            { _pos += 3; return new GoToken(GoTokenKind.Punctuation, three, line, col, start, _pos); }
        }
        if (_pos + 1 < source.Length)
        {
            string two = source.Substring(_pos, 2);
            if (two is ":=" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<-" or "++" or "--" or "+=" or "-=" or "*=" or "/=" or "<<" or ">>")
            { _pos += 2; return new GoToken(GoTokenKind.Punctuation, two, line, col, start, _pos); }
        }
        _pos++;
        return new GoToken(GoTokenKind.Punctuation, source[start.._pos], line, col, start, _pos);
    }
}
