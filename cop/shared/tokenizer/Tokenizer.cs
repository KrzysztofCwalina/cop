namespace Cop.Lang;

public enum TokenKind
{
    Identifier,
    StringLiteral,
    IntLiteral,
    NumberLiteral,
    True,
    False,
    Nic,

    TypeKeyword,
    CollectionKeyword,
    ImportKeyword,
    LetKeyword,
    CommandKeyword,
    ExportKeyword,
    PredicateKeyword,
    FunctionKeyword,
    ForeachKeyword,
    TestKeyword,
    AsyncKeyword,
    RunKeyword,
    FeedKeyword,
    FlagsKeyword,
    EnumKeyword,
    IntrinsicKeyword,
    DoubleColon,
    Colon,
    AndAnd,
    Ampersand,
    Pipe,
    OrOr,
    Not,
    EqualEqual,
    NotEqual,
    Arrow,
    Dot,
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    Equals,
    Comma,
    QuestionMark,
    GreaterThan,
    LessThan,
    GreaterEqual,
    LessEqual,
    Minus,
    Plus,
    Star,
    Slash,
    Percent,
    DocComment,
    Eof
}

public record Token(TokenKind Kind, string Value, int Line);

public class Tokenizer
{
    private readonly string _source;
    private readonly string _filePath;
    private int _pos;
    private int _line = 1;

    public Tokenizer(string source, string filePath = "<unknown>")
    {
        _source = source;
        _filePath = filePath;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (_pos < _source.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _source.Length) break;

            // Check for # comment forms
            if (_source[_pos] == '#')
            {
                var docToken = TryReadHashComment();
                if (docToken != null)
                {
                    tokens.Add(docToken);
                    continue;
                }
                continue; // regular comment was consumed
            }

            tokens.Add(ReadToken());
        }
        tokens.Add(new Token(TokenKind.Eof, "", _line));
        return tokens;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            if (_source[_pos] == '\n') { _line++; _pos++; }
            else if (_source[_pos] == '\r') { _pos++; }
            else if (char.IsWhiteSpace(_source[_pos])) { _pos++; }
            else break;
        }
    }

    // Returns a DocComment token for ##, or null if it was a regular # comment (already consumed).
    private Token? TryReadHashComment()
    {
        int line = _line;

        // ## doc comment
        if (_pos + 1 < _source.Length && _source[_pos + 1] == '#')
        {
            _pos += 2; // skip ##
            // skip optional leading space
            if (_pos < _source.Length && _source[_pos] == ' ') _pos++;
            int start = _pos;
            while (_pos < _source.Length && _source[_pos] != '\n') _pos++;
            string text = _source[start.._pos].TrimEnd('\r');
            return new Token(TokenKind.DocComment, text, line);
        }

        // Single-line # comment (including bare # with no content)
        while (_pos < _source.Length && _source[_pos] != '\n') _pos++;
        return null;
    }

    private Token ReadToken()
    {
        char c = _source[_pos];
        int line = _line;

        if (_pos + 1 < _source.Length)
        {
            string two = _source.Substring(_pos, 2);
            switch (two)
            {
                case "&&": _pos += 2; return new Token(TokenKind.AndAnd, "&&", line);
                case "||": _pos += 2; return new Token(TokenKind.OrOr, "||", line);
                case "==": _pos += 2; return new Token(TokenKind.EqualEqual, "==", line);
                case "!=": _pos += 2; return new Token(TokenKind.NotEqual, "!=", line);
                case ">=": _pos += 2; return new Token(TokenKind.GreaterEqual, ">=", line);
                case "<=": _pos += 2; return new Token(TokenKind.LessEqual, "<=", line);
                case "=>": _pos += 2; return new Token(TokenKind.Arrow, "=>", line);
            }
        }

        switch (c)
        {
            case '&': _pos++; return new Token(TokenKind.Ampersand, "&", line);
            case '|': _pos++; return new Token(TokenKind.Pipe, "|", line);
            case '!': _pos++; return new Token(TokenKind.Not, "!", line);
            case '.': _pos++; return new Token(TokenKind.Dot, ".", line);
            case '(': _pos++; return new Token(TokenKind.LParen, "(", line);
            case ')': _pos++; return new Token(TokenKind.RParen, ")", line);
            case '{': _pos++; return new Token(TokenKind.LBrace, "{", line);
            case '}': _pos++; return new Token(TokenKind.RBrace, "}", line);
            case '[': _pos++; return new Token(TokenKind.LBracket, "[", line);
            case ']': _pos++; return new Token(TokenKind.RBracket, "]", line);
            case '=': _pos++; return new Token(TokenKind.Equals, "=", line);
            case ',': _pos++; return new Token(TokenKind.Comma, ",", line);
            case ':':
                if (_pos + 1 < _source.Length && _source[_pos + 1] == ':')
                {
                    _pos += 2;
                    return new Token(TokenKind.DoubleColon, "::", line);
                }
                _pos++; return new Token(TokenKind.Colon, ":", line);
            case '?': _pos++; return new Token(TokenKind.QuestionMark, "?", line);
            case '>': _pos++; return new Token(TokenKind.GreaterThan, ">", line);
            case '<': _pos++; return new Token(TokenKind.LessThan, "<", line);
            case '-': _pos++; return new Token(TokenKind.Minus, "-", line);
            case '+': _pos++; return new Token(TokenKind.Plus, "+", line);
            case '*': _pos++; return new Token(TokenKind.Star, "*", line);
            case '/': _pos++; return new Token(TokenKind.Slash, "/", line);
            case '%': _pos++; return new Token(TokenKind.Percent, "%", line);
        }

        if (c == '\'')
            return ReadString(line);

        if (char.IsDigit(c))
            return ReadNumber(line);

        if (char.IsLetter(c) || c == '_')
            return ReadIdentifierOrKeyword(line);

        throw new ParseException($"Unexpected character '{c}'", _filePath, line);
    }

    private Token ReadString(int line)
    {
        // Check for triple-quote '''...'''
        if (_pos + 2 < _source.Length && _source[_pos + 1] == '\'' && _source[_pos + 2] == '\'')
            return ReadTripleQuoteString(line);

        _pos++; // skip opening '

        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '\'')
            {
                _pos++;
                return new Token(TokenKind.StringLiteral, sb.ToString(), line);
            }
            if (c == '\\' && _pos + 1 < _source.Length)
            {
                _pos++;
                char next = _source[_pos];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '\\': sb.Append('\\'); break;
                    case '\'': sb.Append('\''); break;
                    case '@': sb.Append('@'); break;
                    default:
                        // Preserve unknown escape sequences (e.g., \b, \s for regex)
                        sb.Append('\\');
                        sb.Append(next);
                        break;
                }
                _pos++;
                continue;
            }
            if (c == '\n') _line++;
            sb.Append(c);
            _pos++;
        }
        throw new ParseException("Unterminated string literal", _filePath, line);
    }

    private Token ReadTripleQuoteString(int line)
    {
        _pos += 3; // skip opening '''

        // Skip the first newline after opening ''' if present
        if (_pos < _source.Length && _source[_pos] == '\r') _pos++;
        if (_pos < _source.Length && _source[_pos] == '\n') { _pos++; _line++; }

        // Read raw content until closing '''
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length)
        {
            if (_source[_pos] == '\'' && _pos + 2 < _source.Length
                && _source[_pos + 1] == '\'' && _source[_pos + 2] == '\'')
            {
                _pos += 3; // skip closing '''
                return new Token(TokenKind.StringLiteral, StripIndent(sb.ToString()), line);
            }
            if (_source[_pos] == '\n') _line++;
            sb.Append(_source[_pos]);
            _pos++;
        }
        throw new ParseException("Unterminated triple-quoted string literal", _filePath, line);
    }

    /// <summary>
    /// Strips common leading whitespace from a triple-quoted string.
    /// The indent of the last line (before closing ''') sets the baseline.
    /// </summary>
    private static string StripIndent(string raw)
    {
        // Find the indent of the last line (which sits before the closing ''')
        int lastNewline = raw.LastIndexOf('\n');
        int indent = 0;
        if (lastNewline >= 0)
        {
            indent = raw.Length - lastNewline - 1;
            // Verify it's all whitespace
            for (int i = lastNewline + 1; i < raw.Length; i++)
            {
                if (raw[i] != ' ' && raw[i] != '\t')
                {
                    indent = 0;
                    break;
                }
            }
        }

        if (indent == 0)
            return raw.TrimEnd();

        // Strip last line (it's just whitespace) and dedent each content line
        var content = raw[..(lastNewline)];
        var lines = content.Split('\n');
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            // Strip trailing \r
            if (l.Length > 0 && l[^1] == '\r') l = l[..^1];
            // Strip indent prefix
            if (l.Length >= indent && l[..indent].Trim().Length == 0)
                l = l[indent..];
            if (i > 0) result.Append('\n');
            result.Append(l);
        }
        return result.ToString();
    }

    private Token ReadNumber(int line)
    {
        int start = _pos;
        while (_pos < _source.Length && char.IsDigit(_source[_pos])) _pos++;
        if (_pos < _source.Length && _source[_pos] == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            _pos++; // skip '.'
            while (_pos < _source.Length && char.IsDigit(_source[_pos])) _pos++;
            return new Token(TokenKind.NumberLiteral, _source[start.._pos], line);
        }
        return new Token(TokenKind.IntLiteral, _source[start.._pos], line);
    }

    private Token ReadIdentifierOrKeyword(int line)
    {
        int start = _pos;
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_' || _source[_pos] == '-'))
            _pos++;
        string value = _source[start.._pos];
        var kind = value switch
        {
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            "nic" => TokenKind.Nic,
            "type" => TokenKind.TypeKeyword,
            "collection" => TokenKind.CollectionKeyword,
            "import" => TokenKind.ImportKeyword,
            "let" => TokenKind.LetKeyword,
            "command" => TokenKind.CommandKeyword,
            "export" => TokenKind.ExportKeyword,
            "predicate" => TokenKind.PredicateKeyword,
            "function" => TokenKind.FunctionKeyword,
            "foreach" => TokenKind.ForeachKeyword,
            "test" => TokenKind.TestKeyword,
            "async" => TokenKind.AsyncKeyword,
            "RUN" => TokenKind.RunKeyword,
            "feed" => TokenKind.FeedKeyword,
            "flags" => TokenKind.FlagsKeyword,
            "enum" => TokenKind.EnumKeyword,
            "intrinsic" => TokenKind.IntrinsicKeyword,

            _ => TokenKind.Identifier
        };
        return new Token(kind, value, line);
    }
}

public class ParseException : Exception
{
    public string FilePath { get; }
    public int Line { get; }
    public int? Column { get; }
    public string? SourceLine { get; }

    public ParseException(string message, string filePath, int line, int? column = null, string? sourceLine = null)
        : base($"{filePath}({line}): {message}")
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        SourceLine = sourceLine;
    }

    /// <summary>
    /// Converts this ParseException into a structured CopDiagnostic.
    /// </summary>
    public CopDiagnostic ToDiagnostic() => new(
        CopDiagnosticSeverity.Error,
        Message.Contains("): ") ? Message[(Message.IndexOf("): ") + 3)..] : Message,
        FilePath,
        Line,
        Column,
        null,
        SourceLine);

    /// <summary>
    /// Extracts a specific line (1-based) from source text.
    /// </summary>
    public static string? GetSourceLine(string source, int line)
    {
        if (string.IsNullOrEmpty(source) || line < 1) return null;
        int currentLine = 1;
        int start = 0;
        while (currentLine < line && start < source.Length)
        {
            if (source[start] == '\n') currentLine++;
            start++;
        }
        if (currentLine != line) return null;
        int end = source.IndexOf('\n', start);
        if (end < 0) end = source.Length;
        var result = source[start..end].TrimEnd('\r');
        return result;
    }
}
