// Tokenizes Java source and projects its declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal class JavaLexer(string source)
{
    private int _pos;
    private int _line = 1;
    private readonly List<(int line, int col, string msg)> _errors = [];
    /// <summary>Gets lexical or parse diagnostics with their source locations.</summary>
    public IReadOnlyList<(int line, int col, string msg)> Errors => _errors;

    private static readonly HashSet<string> s_keywords =
    [
        "abstract", "assert", "boolean", "break", "byte", "case", "catch",
        "char", "class", "const", "continue", "default", "do", "double",
        "else", "enum", "extends", "final", "finally", "float", "for",
        "goto", "if", "implements", "import", "instanceof", "int",
        "interface", "long", "native", "new", "package", "private",
        "protected", "public", "record", "return", "sealed", "short",
        "static", "strictfp", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "try", "var", "void", "volatile",
        "while", "yield", "permits", "non-sealed"
    ];

    /// <summary>
    /// Reads tokens using Java character or Gradle string literal rules.
    /// </summary>
    public List<JavaToken> Tokenize(JavaLiteralDialect dialect = JavaLiteralDialect.Java)
    {
        List<JavaToken> tokens = new List<JavaToken>();
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

            // Annotations
            if (c == '@')
            {
                tokens.Add(ReadAnnotation());
                continue;
            }

            // String literals
            if (c == '"')
            {
                tokens.Add(ReadString());
                continue;
            }

            // Char literal
            if (c == '\'')
            {
                tokens.Add(dialect == JavaLiteralDialect.Gradle ? ReadString('\'') : ReadChar());
                continue;
            }

            // Numbers
            if (char.IsDigit(c))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                tokens.Add(ReadIdentifierOrKeyword());
                continue;
            }

            // Punctuation
            tokens.Add(ReadPunctuation());
        }

        tokens.Add(new JavaToken(JavaTokenKind.Eof, string.Empty, _line, _pos, _pos));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < source.Length)
        {
            char c = source[_pos];
            if (c == '\n') { _line++; _pos++; }
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

    private JavaToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && source[_pos] != '\n')
        {
            _pos++;
        }

        return new JavaToken(JavaTokenKind.LineComment, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadBlockComment()
    {
        int start = _pos;
        int line = _line;
        bool isDoc = _pos + 2 < source.Length && source[_pos + 2] == '*';
        _pos += 2;
        bool closed = false;
        while (_pos < source.Length)
        {
            if (source[_pos] == '*' && _pos + 1 < source.Length && source[_pos + 1] == '/')
            { _pos += 2; closed = true; break; }
            if (source[_pos] == '\n')
            {
                _line++;
            }

            _pos++;
        }
        if (!closed)
        {
            _errors.Add((line, 1, "Unterminated block comment"));
        }

        return new JavaToken(isDoc ? JavaTokenKind.DocComment : JavaTokenKind.BlockComment, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadAnnotation()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip @
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '_' or '.'))
        {
            _pos++;
        }

        if (source[start.._pos] == "@interface")
        {
            return new JavaToken(JavaTokenKind.Keyword, "interface", line, start, _pos);
        }

        int arguments = _pos;
        while (arguments < source.Length && char.IsWhiteSpace(source[arguments]))
        {
            arguments++;
        }

        if (arguments < source.Length && source[arguments] == '(')
        {
            while (_pos < arguments) { if (source[_pos] == '\n') { _line++; } _pos++; }
            int depth = 0;
            while (_pos < source.Length)
            {
                if (source[_pos] is '"' or '\'') { ReadString(source[_pos]); continue; }
                if (_pos + 1 < source.Length && source[_pos] == '/' && source[_pos + 1] == '*')
                { ReadBlockComment(); continue; }
                if (_pos + 1 < source.Length && source[_pos] == '/' && source[_pos + 1] == '/')
                { ReadLineComment(); continue; }
                if (source[_pos] == '(')
                {
                    depth++;
                }
                else if (source[_pos] == ')') { depth--; if (depth == 0) { _pos++; break; } }
                else if (source[_pos] == '\n')
                {
                    _line++;
                }

                _pos++;
            }
        }
        return new JavaToken(JavaTokenKind.Annotation, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadString(char quote = '"')
    {
        int start = _pos;
        int line = _line;
        // Text block """..."""
        if (quote == '"' && _pos + 2 < source.Length && source[_pos + 1] == '"' && source[_pos + 2] == '"')
        {
            _pos += 3;
            bool closed = false;
            while (_pos + 2 < source.Length)
            {
                if (source[_pos] == '"' && source[_pos + 1] == '"' && source[_pos + 2] == '"')
                { _pos += 3; closed = true; break; }
                if (source[_pos] == '\n')
                {
                    _line++;
                }

                _pos++;
            }
            if (!closed)
            {
                _pos = source.Length;
                _errors.Add((line, 1, "Unterminated text block"));
            }
            return new JavaToken(JavaTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
        }
        _pos++; // skip opening "
        bool stringClosed = false;
        while (_pos < source.Length)
        {
            char ch = source[_pos];
            if (ch == '\\') { _pos++; if (_pos < source.Length) { _pos++; } continue; }
            if (ch == quote) { _pos++; stringClosed = true; break; }
            if (ch == '\n')
            {
                break; // unterminated on newline
            }

            _pos++;
        }
        if (!stringClosed)
        {
            _errors.Add((line, 1, "Unterminated string literal"));
        }

        return new JavaToken(JavaTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadChar()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        if (_pos < source.Length && source[_pos] == '\\')
        {
            _pos++;
        }

        if (_pos < source.Length)
        {
            _pos++;
        }

        if (_pos < source.Length && source[_pos] == '\'')
        {
            _pos++;
        }

        return new JavaToken(JavaTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '.' or '_'))
        {
            _pos++;
        }

        return new JavaToken(JavaTokenKind.NumberLiteral, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadIdentifierOrKeyword()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '_' or '$'))
        {
            _pos++;
        }

        if (source[start.._pos] == "non"
            && _pos + 7 <= source.Length
            && source[_pos] == '-'
            && source.AsSpan(_pos + 1, 6).SequenceEqual("sealed".AsSpan())
            && (_pos + 7 == source.Length || !(char.IsLetterOrDigit(source[_pos + 7]) || source[_pos + 7] is '_' or '$')))
        {
            _pos += 7;
        }
        string value = source[start.._pos];
        JavaTokenKind kind = s_keywords.Contains(value) ? JavaTokenKind.Keyword : JavaTokenKind.Identifier;
        return new JavaToken(kind, value, line, start, _pos);
    }

    private JavaToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        if (_pos + 2 < source.Length)
        {
            string three = source.Substring(_pos, 3);
            if (three is ">>>" or "...") { _pos += 3; return new JavaToken(JavaTokenKind.Punctuation, three, line, start, _pos); }
        }
        if (_pos + 1 < source.Length)
        {
            string two = source.Substring(_pos, 2);
            if (two is "::" or "->" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "++" or "--" or "+=" or "-=" or "*=" or "/=" or "<<" or ">>" or "|=" or "&=" or "^=")
            { _pos += 2; return new JavaToken(JavaTokenKind.Punctuation, two, line, start, _pos); }
        }
        _pos++;
        return new JavaToken(JavaTokenKind.Punctuation, source[start.._pos], line, start, _pos);
    }
}
