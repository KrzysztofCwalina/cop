namespace TypeSpecProvider;

public enum TspTokenKind
{
    // Literals
    Identifier,
    StringLiteral,
    NumericLiteral,
    TripleQuoteString,

    // Keywords
    Model,
    Op,
    Interface,
    Enum,
    Union,
    Scalar,
    Namespace,
    Import,
    Using,
    Extends,
    Is,
    If,
    Else,
    Alias,
    Extern,
    Dec,
    Fn,
    Const,
    True,
    False,
    Void,
    Never,
    Unknown,

    // Punctuation
    OpenBrace,
    CloseBrace,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Comma,
    Colon,
    Dot,
    DotDotDot,
    At,
    Hash,
    QuestionMark,
    Equals,
    Pipe,
    Ampersand,
    Arrow,        // =>

    // Special
    EndOfFile,
    Unknown_Token,
}

public record TspToken(TspTokenKind Kind, string Value, int Line, int Column);

/// <summary>
/// Tokenizer for TypeSpec source files.
/// </summary>
public class TspLexer
{
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public TspLexer(string source)
    {
        _source = source;
    }

    public List<TspToken> Tokenize()
    {
        var tokens = new List<TspToken>();
        while (_pos < _source.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _source.Length) break;

            var token = ReadToken();
            if (token is not null)
                tokens.Add(token);
        }
        tokens.Add(new TspToken(TspTokenKind.EndOfFile, "", _line, _col));
        return tokens;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            // Single-line comment
            if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
            {
                while (_pos < _source.Length && _source[_pos] != '\n')
                    Advance();
                continue;
            }

            // Multi-line comment
            if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '*')
            {
                Advance(); Advance(); // skip /*
                while (_pos + 1 < _source.Length && !(_source[_pos] == '*' && _source[_pos + 1] == '/'))
                    Advance();
                if (_pos + 1 < _source.Length)
                {
                    Advance(); Advance(); // skip */
                }
                continue;
            }

            break;
        }
    }

    private TspToken? ReadToken()
    {
        int startLine = _line, startCol = _col;
        char c = _source[_pos];

        // Decorators
        if (c == '@')
        {
            Advance();
            return new TspToken(TspTokenKind.At, "@", startLine, startCol);
        }

        // Hash (for object literals like #{ ... })
        if (c == '#')
        {
            Advance();
            return new TspToken(TspTokenKind.Hash, "#", startLine, startCol);
        }

        // Triple-quoted string
        if (c == '"' && _pos + 2 < _source.Length && _source[_pos + 1] == '"' && _source[_pos + 2] == '"')
        {
            return ReadTripleQuotedString(startLine, startCol);
        }

        // String literal
        if (c == '"')
        {
            return ReadString(startLine, startCol);
        }

        // Numeric literal
        if (char.IsDigit(c) || (c == '-' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1])))
        {
            return ReadNumber(startLine, startCol);
        }

        // Identifier or keyword
        if (char.IsLetter(c) || c == '_' || c == '$')
        {
            return ReadIdentifierOrKeyword(startLine, startCol);
        }

        // Punctuation
        return ReadPunctuation(startLine, startCol);
    }

    private TspToken ReadString(int startLine, int startCol)
    {
        Advance(); // skip opening "
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length && _source[_pos] != '"')
        {
            if (_source[_pos] == '\\' && _pos + 1 < _source.Length)
            {
                Advance(); // skip backslash
                sb.Append(_source[_pos]);
            }
            else
            {
                sb.Append(_source[_pos]);
            }
            Advance();
        }
        if (_pos < _source.Length) Advance(); // skip closing "
        return new TspToken(TspTokenKind.StringLiteral, sb.ToString(), startLine, startCol);
    }

    private TspToken ReadTripleQuotedString(int startLine, int startCol)
    {
        Advance(); Advance(); Advance(); // skip """
        var sb = new System.Text.StringBuilder();
        while (_pos + 2 < _source.Length && !(_source[_pos] == '"' && _source[_pos + 1] == '"' && _source[_pos + 2] == '"'))
        {
            sb.Append(_source[_pos]);
            Advance();
        }
        if (_pos + 2 < _source.Length) { Advance(); Advance(); Advance(); } // skip closing """
        return new TspToken(TspTokenKind.TripleQuoteString, sb.ToString().Trim(), startLine, startCol);
    }

    private TspToken ReadNumber(int startLine, int startCol)
    {
        var sb = new System.Text.StringBuilder();
        if (_source[_pos] == '-') { sb.Append('-'); Advance(); }
        while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
        {
            sb.Append(_source[_pos]);
            Advance();
        }
        return new TspToken(TspTokenKind.NumericLiteral, sb.ToString(), startLine, startCol);
    }

    private TspToken ReadIdentifierOrKeyword(int startLine, int startCol)
    {
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_' || _source[_pos] == '$'))
        {
            sb.Append(_source[_pos]);
            Advance();
        }

        var value = sb.ToString();
        var kind = value switch
        {
            "model" => TspTokenKind.Model,
            "op" => TspTokenKind.Op,
            "interface" => TspTokenKind.Interface,
            "enum" => TspTokenKind.Enum,
            "union" => TspTokenKind.Union,
            "scalar" => TspTokenKind.Scalar,
            "namespace" => TspTokenKind.Namespace,
            "import" => TspTokenKind.Import,
            "using" => TspTokenKind.Using,
            "extends" => TspTokenKind.Extends,
            "is" => TspTokenKind.Is,
            "if" => TspTokenKind.If,
            "else" => TspTokenKind.Else,
            "alias" => TspTokenKind.Alias,
            "extern" => TspTokenKind.Extern,
            "dec" => TspTokenKind.Dec,
            "fn" => TspTokenKind.Fn,
            "const" => TspTokenKind.Const,
            "true" => TspTokenKind.True,
            "false" => TspTokenKind.False,
            "void" => TspTokenKind.Void,
            "never" => TspTokenKind.Never,
            "unknown" => TspTokenKind.Unknown,
            _ => TspTokenKind.Identifier,
        };

        return new TspToken(kind, value, startLine, startCol);
    }

    private TspToken ReadPunctuation(int startLine, int startCol)
    {
        char c = _source[_pos];
        Advance();

        switch (c)
        {
            case '{': return new TspToken(TspTokenKind.OpenBrace, "{", startLine, startCol);
            case '}': return new TspToken(TspTokenKind.CloseBrace, "}", startLine, startCol);
            case '(': return new TspToken(TspTokenKind.OpenParen, "(", startLine, startCol);
            case ')': return new TspToken(TspTokenKind.CloseParen, ")", startLine, startCol);
            case '[': return new TspToken(TspTokenKind.OpenBracket, "[", startLine, startCol);
            case ']': return new TspToken(TspTokenKind.CloseBracket, "]", startLine, startCol);
            case ';': return new TspToken(TspTokenKind.Semicolon, ";", startLine, startCol);
            case ',': return new TspToken(TspTokenKind.Comma, ",", startLine, startCol);
            case ':': return new TspToken(TspTokenKind.Colon, ":", startLine, startCol);
            case '?': return new TspToken(TspTokenKind.QuestionMark, "?", startLine, startCol);
            case '|': return new TspToken(TspTokenKind.Pipe, "|", startLine, startCol);
            case '&': return new TspToken(TspTokenKind.Ampersand, "&", startLine, startCol);
            case '=':
                if (_pos < _source.Length && _source[_pos] == '>')
                {
                    Advance();
                    return new TspToken(TspTokenKind.Arrow, "=>", startLine, startCol);
                }
                return new TspToken(TspTokenKind.Equals, "=", startLine, startCol);
            case '.':
                if (_pos + 1 < _source.Length && _source[_pos] == '.' && _source[_pos + 1] == '.')
                {
                    Advance(); Advance();
                    return new TspToken(TspTokenKind.DotDotDot, "...", startLine, startCol);
                }
                return new TspToken(TspTokenKind.Dot, ".", startLine, startCol);
            default:
                return new TspToken(TspTokenKind.Unknown_Token, c.ToString(), startLine, startCol);
        }
    }

    private void Advance()
    {
        if (_pos < _source.Length)
        {
            if (_source[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            _pos++;
        }
    }
}
