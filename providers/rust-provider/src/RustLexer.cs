// Selects pinned frontend or source-only Rust parsing for the shared code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal class RustLexer(string source)
{
    private int _pos;
    private int _line = 1;
    private readonly List<(int line, int col, string msg)> _errors = [];
    /// <summary>Gets lexical or parse diagnostics with their source locations.</summary>
    public IReadOnlyList<(int line, int col, string msg)> Errors => _errors;

    private int ColAt(int pos)
    {
        int c = 1;
        for (int i = pos - 1; i >= 0 && source[i] != '\n'; i--)
        {
            c++;
        }

        return c;
    }

    private static readonly HashSet<string> s_keywords =
    [
        "as", "async", "await", "break", "const", "continue", "crate", "dyn",
        "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in",
        "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return",
        "self", "Self", "static", "struct", "super", "trait", "true", "type",
        "unsafe", "use", "where", "while", "yield"
    ];

    /// <summary>Reads Rust tokens with source offsets and line numbers.</summary>
    public List<RustToken> Tokenize()
    {
        List<RustToken> tokens = new List<RustToken>();
        while (_pos < source.Length)
        {
            SkipWhitespace();
            if (_pos >= source.Length)
            {
                break;
            }

            char c = source[_pos];

            // Line/doc/block comments
            if (c == '/' && _pos + 1 < source.Length)
            {
                if (source[_pos + 1] == '/')
                {
                    RustToken token = ReadLineComment();
                    tokens.Add(token);
                    continue;
                }
                if (source[_pos + 1] == '*')
                {
                    RustToken token = ReadBlockComment();
                    tokens.Add(token);
                    continue;
                }
            }

            // Attributes: #[...] or #![...]
            if (c == '#' && _pos + 1 < source.Length && (source[_pos + 1] == '[' || (source[_pos + 1] == '!' && _pos + 2 < source.Length && source[_pos + 2] == '[')))
            {
                tokens.Add(ReadAttribute());
                continue;
            }

            // Byte/C strings have the same delimiters but are not Rust string values.
            if ((c is 'b' or 'c') && _pos + 1 < source.Length && source[_pos + 1] == '"')
            {
                int start = _pos++;
                RustToken literal = ReadStringLiteral();
                tokens.Add(literal with { Value = source[start.._pos], Start = start });
                continue;
            }
            if ((c is 'b' or 'c') && _pos + 2 < source.Length && source[_pos + 1] == 'r'
                && source[_pos + 2] is '"' or '#')
            {
                int start = _pos++;
                RustToken literal = ReadRawString();
                tokens.Add(literal with { Value = source[start.._pos], Start = start });
                continue;
            }
            if (c == 'b' && _pos + 1 < source.Length && source[_pos + 1] == '\'')
            {
                int start = _pos++;
                RustToken literal = ReadCharLiteral();
                tokens.Add(literal with { Value = source[start.._pos], Start = start });
                continue;
            }
            if (c == '"')
            {
                tokens.Add(ReadStringLiteral());
                continue;
            }

            // Raw strings: r"..." r#"..."#  — but NOT raw identifiers r#ident
            if (c == 'r' && _pos + 1 < source.Length && source[_pos + 1] == '"')
            {
                tokens.Add(ReadRawString());
                continue;
            }
            if (c == 'r' && _pos + 1 < source.Length && source[_pos + 1] == '#')
            {
                // A raw string has a '"' after the run of '#'; a raw identifier (r#ident)
                // has an identifier char there instead.
                int k = _pos + 1;
                while (k < source.Length && source[k] == '#')
                {
                    k++;
                }

                if (k < source.Length && source[k] == '"')
                {
                    tokens.Add(ReadRawString());
                    continue;
                }
                if (k < source.Length && (char.IsLetter(source[k]) || source[k] == '_'))
                {
                    tokens.Add(ReadRawIdentifier());
                    continue;
                }
            }

            // Char literal vs lifetime: 'x' / '\n' are char literals; 'a / 'static / '_ are lifetimes.
            if (c == '\'')
            {
                bool isChar = (_pos + 1 < source.Length && source[_pos + 1] == '\\')      // escaped: '\n'
                    || (_pos + 2 < source.Length && source[_pos + 2] == '\'')
                    || (_pos + 3 < source.Length && char.IsSurrogatePair(source, _pos + 1) && source[_pos + 3] == '\'');
                if (isChar) { tokens.Add(ReadCharLiteral()); continue; }
                tokens.Add(ReadLifetime());
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

            // Punctuation (single or multi-char)
            tokens.Add(ReadPunctuation());
        }

        tokens.Add(new RustToken(RustTokenKind.Eof, string.Empty, _line, _pos, _pos));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < source.Length)
        {
            char ch = source[_pos];
            if (ch is ' ' or '\t' or '\r') { _pos++; }
            else if (ch == '\n') { _line++; _pos++; }
            else
            {
                break;
            }
        }
    }

    private RustToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        _pos += 2; // skip //
        // /// is an outer doc, //! is an inner doc, but //// (or more) is a plain comment.
        bool isDoc = false;
        if (_pos < source.Length)
        {
            char c3 = source[_pos];
            if (c3 == '!')
            {
                isDoc = true;
            }
            else if (c3 == '/' && (_pos + 1 >= source.Length || source[_pos + 1] != '/'))
            {
                isDoc = true;
            }
        }
        while (_pos < source.Length && source[_pos] != '\n')
        {
            _pos++;
        }

        string value = source[start.._pos];
        return new RustToken(isDoc ? RustTokenKind.DocComment : RustTokenKind.LineComment, value, line, start, _pos);
    }

    private RustToken ReadBlockComment()
    {
        int start = _pos;
        int line = _line;
        int depth = 0;
        while (_pos < source.Length)
        {
            if (source[_pos] == '/' && _pos + 1 < source.Length && source[_pos + 1] == '*')
            { depth++; _pos += 2; }
            else if (source[_pos] == '*' && _pos + 1 < source.Length && source[_pos + 1] == '/')
            { depth--; _pos += 2; if (depth == 0)
                {
                    break;
                }
            }
            else
            {
                if (source[_pos] == '\n')
                {
                    _line++;
                }

                _pos++;
            }
        }
        if (depth > 0)
        {
            _errors.Add((line, ColAt(start), "unterminated block comment"));
        }

        string value = source[start.._pos];
        // /** ... */ is an outer doc and /*! ... */ an inner doc; /**/ and /*** ... */ are not.
        RustTokenKind kind = RustTokenKind.BlockComment;
        if (value.Length >= 3 && value[1] == '*')
        {
            if (value[2] == '!')
            {
                kind = RustTokenKind.DocComment;
            }
            else if (value[2] == '*' && (value.Length < 4 || (value[3] != '*' && value[3] != '/')))
            {
                kind = RustTokenKind.DocComment;
            }
        }
        return new RustToken(kind, value, line, start, _pos);
    }

    private RustToken ReadAttribute()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip #
        if (_pos < source.Length && source[_pos] == '!')
        {
            _pos++;
        }

        if (_pos < source.Length && source[_pos] == '[')
        {
            int depth = 0;
            while (_pos < source.Length)
            {
                if (source[_pos] == '"') { ReadStringLiteral(); continue; }
                if (source[_pos] == 'r' && IsRawStringStart(_pos))
                { ReadRawString(); continue; }
                if (source[_pos] == '/' && _pos + 1 < source.Length && source[_pos + 1] == '*')
                { ReadBlockComment(); continue; }
                if (source[_pos] == '[')
                {
                    depth++;
                }
                else if (source[_pos] == ']') { depth--; if (depth == 0) { _pos++; break; } }
                else if (source[_pos] == '\n')
                {
                    _line++;
                }

                _pos++;
            }
            if (depth > 0)
            {
                _errors.Add((line, ColAt(start), "unterminated attribute"));
            }
        }
        return new RustToken(RustTokenKind.Attribute, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadStringLiteral()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip opening "
        while (_pos < source.Length && source[_pos] != '"')
        {
            if (source[_pos] == '\\' && _pos + 1 < source.Length)
            {
                _pos++; // skip escape
            }

            if (_pos < source.Length && source[_pos] == '\n')
            {
                _line++;
            }

            _pos++;
        }
        if (_pos < source.Length)
        {
            _pos++; // skip closing "
        }
        else
        {
            _errors.Add((line, ColAt(start), "unterminated string literal"));
        }

        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadRawString()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip r
        int hashes = 0;
        bool closed = false;
        while (_pos < source.Length && source[_pos] == '#') { hashes++; _pos++; }
        if (_pos < source.Length && source[_pos] == '"')
        {
            _pos++; // skip "
        }
        // Find closing "###
        while (_pos < source.Length)
        {
            if (source[_pos] == '"')
            {
                _pos++;
                int endHashes = 0;
                while (_pos < source.Length && source[_pos] == '#' && endHashes < hashes) { endHashes++; _pos++; }
                if (endHashes == hashes) { closed = true; break; }
            }
            else
            {
                if (source[_pos] == '\n')
                {
                    _line++;
                }

                _pos++;
            }
        }
        if (!closed)
        {
            _errors.Add((line, ColAt(start), "unterminated raw string literal"));
        }

        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadCharLiteral()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        while (_pos < source.Length && source[_pos] != '\'' && source[_pos] != '\n')
        {
            if (source[_pos] == '\\' && _pos + 1 < source.Length)
            {
                _pos++;
            }

            _pos++;
        }
        if (_pos < source.Length && source[_pos] == '\'')
        {
            _pos++;
        }
        else
        {
            _errors.Add((line, ColAt(start), "unterminated character literal"));
        }

        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadLifetime()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
        {
            _pos++;
        }

        return new RustToken(RustTokenKind.Lifetime, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadRawIdentifier()
    {
        int start = _pos;
        int line = _line;
        _pos += 2; // skip r#
        int nameStart = _pos;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
        {
            _pos++;
        }
        // A raw identifier is always an identifier (never a keyword) — that is its purpose.
        return new RustToken(RustTokenKind.Identifier, source[nameStart.._pos], line, start, _pos);
    }

    private RustToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length)
        {
            char c = source[_pos];
            if (char.IsLetterOrDigit(c) || c == '_') { _pos++; continue; }
            if (c == '.' && _pos + 1 < source.Length && char.IsDigit(source[_pos + 1]))
            { _pos++; continue; }
            if ((c is '+' or '-') && _pos > start && source[_pos - 1] is 'e' or 'E'
                && !(source[start] == '0' && start + 1 < source.Length && source[start + 1] is 'x' or 'o' or 'b'))
            { _pos++; continue; }
            break;
        }
        return new RustToken(RustTokenKind.NumberLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadIdentifierOrKeyword()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
        {
            _pos++;
        }

        string value = source[start.._pos];
        RustTokenKind kind = s_keywords.Contains(value) ? RustTokenKind.Keyword : RustTokenKind.Identifier;
        return new RustToken(kind, value, line, start, _pos);
    }

    private bool IsRawStringStart(int start)
    {
        int position = start + 1;
        while (position < source.Length && source[position] == '#')
        {
            position++;
        }

        return position < source.Length && source[position] == '"';
    }

    private RustToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        // Multi-char operators
        if (_pos + 1 < source.Length)
        {
            string two = source.Substring(_pos, 2);
            if (two == ".." && _pos + 2 < source.Length && source[_pos + 2] is '.' or '=')
            {
                _pos += 3;
                return new RustToken(RustTokenKind.Punctuation, source[start.._pos], line, start, _pos);
            }
            if (two is "::" or "->" or "=>" or "&&" or "||" or "==" or "!=" or ">=" or "<=" or ".." or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=")
            {
                _pos += 2;
                return new RustToken(RustTokenKind.Punctuation, two, line, start, _pos);
            }
        }
        _pos++;
        return new RustToken(RustTokenKind.Punctuation, source[start.._pos], line, start, _pos);
    }
}
