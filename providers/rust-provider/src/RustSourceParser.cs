using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class RustSourceParser : ISourceParser
{
    private readonly Lazy<RustFrontend?> _frontend;
    public RustSourceParser(string? frontendPath = null, string? projectRoot = null, string? standardLibraryDirectory = null,
        Func<Stream?>? bundleFactory = null, string? cacheDirectory = null)
    {
        _frontend = new(() =>
        {
            string? executable = frontendPath ?? RustFrontendBundle.Find(bundleFactory, cacheDirectory);
            return executable is not null ? new(executable, projectRoot ?? Directory.GetCurrentDirectory(), standardLibraryDirectory) : null;
        });
    }

    public override IReadOnlyList<string> Extensions => [".rs"];
    public override string Language => "rust";
    public override bool CanCache(string filePath) => _frontend.Value is null;
    public override string GetCacheIdentity(string filePath) =>
        base.GetCacheIdentity(filePath) + ":" + (_frontend.Value?.Executable ?? "structural-fallback");

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        try
        {
            if (_frontend.Value is { } frontend) return frontend.Parse(filePath, sourceText);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new(filePath, "rust", [], [], sourceText) { ParseErrors = [$"{filePath}: error: {ex.Message}"] };
        }
        var lexer = new RustLexer(sourceText);
        var tokens = lexer.Tokenize();
        var parser = new RustParser(tokens, sourceText, lexer.Errors);
        return parser.Parse(filePath);
    }
}

#region Lexer

internal enum RustTokenKind
{
    Identifier, Keyword, Punctuation, StringLiteral, NumberLiteral,
    LineComment, DocComment, BlockComment, Attribute, Lifetime, Eof
}

internal record struct RustToken(RustTokenKind Kind, string Value, int Line, int Start, int End);

internal class RustLexer(string source)
{
    private int _pos;
    private int _line = 1;
    private readonly List<(int line, int col, string msg)> _errors = [];
    public IReadOnlyList<(int line, int col, string msg)> Errors => _errors;

    private int ColAt(int pos)
    {
        int c = 1;
        for (int i = pos - 1; i >= 0 && source[i] != '\n'; i--)
            c++;
        return c;
    }

    private static readonly HashSet<string> Keywords =
    [
        "as", "async", "await", "break", "const", "continue", "crate", "dyn",
        "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in",
        "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return",
        "self", "Self", "static", "struct", "super", "trait", "true", "type",
        "unsafe", "use", "where", "while", "yield"
    ];

    public List<RustToken> Tokenize()
    {
        var tokens = new List<RustToken>();
        while (_pos < source.Length)
        {
            SkipWhitespace();
            if (_pos >= source.Length) break;

            char c = source[_pos];

            // Line/doc/block comments
            if (c == '/' && _pos + 1 < source.Length)
            {
                if (source[_pos + 1] == '/')
                {
                    var token = ReadLineComment();
                    tokens.Add(token);
                    continue;
                }
                if (source[_pos + 1] == '*')
                {
                    var token = ReadBlockComment();
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
                var literal = ReadStringLiteral();
                tokens.Add(literal with { Value = source[start.._pos], Start = start });
                continue;
            }
            if ((c is 'b' or 'c') && _pos + 2 < source.Length && source[_pos + 1] == 'r'
                && source[_pos + 2] is '"' or '#')
            {
                int start = _pos++;
                var literal = ReadRawString();
                tokens.Add(literal with { Value = source[start.._pos], Start = start });
                continue;
            }
            if (c == 'b' && _pos + 1 < source.Length && source[_pos + 1] == '\'')
            {
                int start = _pos++;
                var literal = ReadCharLiteral();
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
                while (k < source.Length && source[k] == '#') k++;
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

        tokens.Add(new RustToken(RustTokenKind.Eof, "", _line, _pos, _pos));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < source.Length)
        {
            char ch = source[_pos];
            if (ch is ' ' or '\t' or '\r') { _pos++; }
            else if (ch == '\n') { _line++; _pos++; }
            else break;
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
            if (c3 == '!') isDoc = true;
            else if (c3 == '/' && (_pos + 1 >= source.Length || source[_pos + 1] != '/')) isDoc = true;
        }
        while (_pos < source.Length && source[_pos] != '\n')
            _pos++;
        var value = source[start.._pos];
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
            { depth--; _pos += 2; if (depth == 0) break; }
            else
            {
                if (source[_pos] == '\n') _line++;
                _pos++;
            }
        }
        if (depth > 0) _errors.Add((line, ColAt(start), "unterminated block comment"));
        var value = source[start.._pos];
        // /** ... */ is an outer doc and /*! ... */ an inner doc; /**/ and /*** ... */ are not.
        RustTokenKind kind = RustTokenKind.BlockComment;
        if (value.Length >= 3 && value[1] == '*')
        {
            if (value[2] == '!')
                kind = RustTokenKind.DocComment;
            else if (value[2] == '*' && (value.Length < 4 || (value[3] != '*' && value[3] != '/')))
                kind = RustTokenKind.DocComment;
        }
        return new RustToken(kind, value, line, start, _pos);
    }

    private RustToken ReadAttribute()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip #
        if (_pos < source.Length && source[_pos] == '!') _pos++;
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
                if (source[_pos] == '[') depth++;
                else if (source[_pos] == ']') { depth--; if (depth == 0) { _pos++; break; } }
                else if (source[_pos] == '\n') _line++;
                _pos++;
            }
            if (depth > 0) _errors.Add((line, ColAt(start), "unterminated attribute"));
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
            if (source[_pos] == '\\' && _pos + 1 < source.Length) _pos++; // skip escape
            if (_pos < source.Length && source[_pos] == '\n') _line++;
            _pos++;
        }
        if (_pos < source.Length) _pos++; // skip closing "
        else _errors.Add((line, ColAt(start), "unterminated string literal"));
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
        if (_pos < source.Length && source[_pos] == '"') _pos++; // skip "
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
                if (source[_pos] == '\n') _line++;
                _pos++;
            }
        }
        if (!closed)
            _errors.Add((line, ColAt(start), "unterminated raw string literal"));
        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadCharLiteral()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        while (_pos < source.Length && source[_pos] != '\'' && source[_pos] != '\n')
        {
            if (source[_pos] == '\\' && _pos + 1 < source.Length) _pos++;
            _pos++;
        }
        if (_pos < source.Length && source[_pos] == '\'') _pos++;
        else _errors.Add((line, ColAt(start), "unterminated character literal"));
        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadLifetime()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            _pos++;
        return new RustToken(RustTokenKind.Lifetime, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadRawIdentifier()
    {
        int start = _pos;
        int line = _line;
        _pos += 2; // skip r#
        int nameStart = _pos;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            _pos++;
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
            _pos++;
        var value = source[start.._pos];
        var kind = Keywords.Contains(value) ? RustTokenKind.Keyword : RustTokenKind.Identifier;
        return new RustToken(kind, value, line, start, _pos);
    }

    private bool IsRawStringStart(int start)
    {
        int position = start + 1;
        while (position < source.Length && source[position] == '#') position++;
        return position < source.Length && source[position] == '"';
    }

    private RustToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        // Multi-char operators
        if (_pos + 1 < source.Length)
        {
            var two = source.Substring(_pos, 2);
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

#endregion

#region Parser

internal class RustParser(List<RustToken> tokens, string sourceText, IReadOnlyList<(int line, int col, string msg)> lexerErrors)
{
    private int _pos;
    private string _scope = "";
    private readonly List<(MethodDeclaration method, string scope)> _nestedFunctions = [];
    private readonly List<StatementInfo> _nestedStatements = [];
    private readonly Dictionary<int, int> _delimiterPairs = RustBodyParser.BuildPairs(tokens);
    private int _functionDepth;
    private readonly Stack<int> _externBlocks = new();
    private readonly List<ImportInfo> _imports = [];
    private readonly List<(int line, int col, string msg)> _errors = [..lexerErrors];

    private void AddError(int line, int col, string msg) => _errors.Add((line, col, msg));

    private int ColAt(int charPos)
    {
        int c = 1;
        for (int i = charPos - 1; i >= 0 && sourceText[i] != '\n'; i--)
            c++;
        return c;
    }

    // Reports unbalanced (), [], {} delimiters. Operates on Punctuation tokens only — brackets
    // inside strings, char literals, comments, and attributes are separate token kinds, so they
    // are never miscounted. Generics `<>` are intentionally NOT balanced (ambiguous with the
    // comparison/shift operators). Pops on any closer to avoid cascading errors, reporting
    // mismatches as well as a missing closer (openers remain) or a stray closer (empty stack)
    // without false positives on valid Rust (whose () [] {} token trees are always balanced).
    private void CheckDelimiterBalance()
    {
        var stack = new Stack<RustToken>();
        foreach (var t in tokens)
        {
            if (t.Kind != RustTokenKind.Punctuation) continue;
            if (t.Value is "(" or "[" or "{") stack.Push(t);
            else if (t.Value is ")" or "]" or "}")
            {
                if (stack.Count == 0) AddError(t.Line, ColAt(t.Start), $"unmatched '{t.Value}'");
                else
                {
                    var open = stack.Pop();
                    if ((open.Value, t.Value) is not ("(", ")") and not ("[", "]") and not ("{", "}"))
                        AddError(t.Line, ColAt(t.Start), $"mismatched '{t.Value}' for '{open.Value}' at line {open.Line}");
                }
            }
        }
        foreach (var open in stack)
            AddError(open.Line, ColAt(open.Start), $"unclosed '{open.Value}'");
    }

    public SourceFile Parse(string filePath)
    {
        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var usings = new List<string>();
        var freeFunctions = new List<MethodDeclaration>();
        var typeScopes = new Dictionary<TypeDeclaration, string>(ReferenceEqualityComparer.Instance);
        var functionScopes = new Dictionary<MethodDeclaration, string>(ReferenceEqualityComparer.Instance);
        var modules = new Stack<(string path, int end)>();

        while (!IsAtEnd())
        {
            while (modules.TryPeek(out var module) && _pos >= module.end) modules.Pop();
            while (_externBlocks.TryPeek(out int externEnd) && _pos >= externEnd) _externBlocks.Pop();
            string scope = _scope = modules.TryPeek(out var currentModule) ? currentModule.path : "";
            int loopStart = _pos;
            int functionStart = freeFunctions.Count;
            SkipCommentsAndAttributes();
            if (IsAtEnd()) break;
            int itemStart = _pos;

            // Skip `#[cfg(test)] mod tests { ... }` — its contents are test code, not production.
            if (TryEnterExternBlock()) continue;
            if (CheckKeyword("mod") && PrecededByCfgTest())
            {
                SkipModuleBody();
            }

            else if (MatchKeyword("mod"))
            {
                string name = ConsumeIdentifier();
                if (Check("{"))
                {
                    int open = _pos;
                    if (modules.Count >= 128)
                    {
                        AddError(CurrentLine(), ColAt(Current().Start), "module nesting limit (128) exceeded");
                        SkipBraces();
                    }
                    else
                    {
                        int close = _delimiterPairs.GetValueOrDefault(open, tokens.Count - 1);
                        _pos = open + 1;
                        modules.Push((scope.Length > 0 ? scope + "::" + name : name, close));
                    }
                }
                else if (Check(";")) Advance();
            }
            // Skip `macro_rules! name { ... }` — its body is a template, not real code.
            else if (CheckIdent("macro_rules") && Peek().Value == "!")
            {
                Advance(); Advance();        // macro_rules !
                ConsumeIdentifier();         // macro name
                if (Check("{")) SkipBraces();
                else if (Check("(")) { SkipParens(); if (Check(";")) Advance(); }
                else if (Check("[")) { SkipBrackets(); if (Check(";")) Advance(); }
            }
            else if (MatchKeyword("use"))
            {
                ParseUse(usings);
            }
            else if (CheckKeyword("struct") || CheckKeyword("enum") || CheckKeyword("trait") || CheckKeyword("type")
                || CheckKeyword("impl") || CheckKeyword("pub") || CheckKeyword("unsafe")
                || (CheckIdent("union") && Peek().Kind == RustTokenKind.Identifier))
            {
                var type = TryParseTypeOrImpl(statements, freeFunctions);
                if (type != null)
                {
                    type = type with
                    {
                        Line = tokens[itemStart].Line,
                        Column = ColAt(tokens[itemStart].Start),
                        Namespace = scope.Length > 0 ? scope : null,
                        EndLine = PreviousLine(),
                        EndColumn = ColAt(tokens[Math.Max(0, _pos - 1)].End)
                    };
                    types.Add(type);
                    typeScopes.Add(type, scope);
                }
                // If null (e.g. a `pub fn` free function), the function was parsed in place and
                // collected into freeFunctions; or the position was reset and the global
                // progress guard below advances past the unrecognized token.
            }
            else if ((CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("const")
                || CheckKeyword("extern")) && IsFnAhead())
            {
                var (m, _) = ParseFn(statements);
                if (m != null) freeFunctions.Add(m);
            }

            for (int i = functionStart; i < freeFunctions.Count; i++)
                functionScopes[freeFunctions[i]] = scope;
            // Guarantee forward progress on every iteration — never spin on a token that
            // no branch consumed (stray punctuation, top-level const/mod/type, etc.).
            if (_pos == loopStart) Advance();
        }

        // Surface unbalanced delimiters (a missing/stray ( [ { } ] )) — a common malformed-source
        // case the structural parser alone would otherwise tolerate silently.
        CheckDelimiterBalance();

        statements.AddRange(_nestedStatements);
        statements = statements.OrderBy(s => s.Line).ThenBy(s => s.Column).ToList();
        foreach (var (method, scope) in _nestedFunctions)
        {
            freeFunctions.Add(method);
            functionScopes[method] = scope;
        }
        // Free functions are part of a module's API surface but don't belong to a declared
        // type, so expose them as methods of a synthetic per-file container (a Class, which
        // type-level naming/doc checks deliberately skip).
        if (freeFunctions.Count > 0)
        {
            foreach (var group in freeFunctions.GroupBy(m => functionScopes[m]))
            {
                var moduleName = (group.Key.Length > 0 ? group.Key : System.IO.Path.GetFileNameWithoutExtension(filePath)) + " (functions)";
                types.Add(new TypeDeclaration(moduleName, TypeKind.Class, Modifier.Public, [], [], [], group.ToList(), [], [], group.First().Line)
                { Namespace = group.Key.Length > 0 ? group.Key : null }.AsRust());
            }
        }

        AttachImplMethods(types, typeScopes);
        foreach (var method in types.SelectMany(t => t.Methods))
            foreach (var statement in method.Statements) statement.Method = method;
        return new SourceFile(filePath, "rust", types, statements, sourceText)
        {
            Usings = usings,
            Imports = _imports,
            Regions = [],
            CommentLines = ExtractCommentLines(),
            ParseErrors = _errors.Select(e => $"{filePath}({e.line},{e.col}): error: {e.msg}").ToList()
        };
    }

    private bool TryEnterExternBlock()
    {
        int saved = _pos;
        ReadVisibility();
        MatchKeyword("unsafe");
        if (MatchKeyword("extern"))
        {
            if (Current().Kind == RustTokenKind.StringLiteral) Advance();
            if (Check("{") && _delimiterPairs.TryGetValue(_pos, out int end))
            {
                _externBlocks.Push(end);
                Advance();
                return true;
            }
        }
        _pos = saved;
        return false;
    }

    private static void AttachImplMethods(List<TypeDeclaration> types, Dictionary<TypeDeclaration, string> scopes)
    {
        foreach (var impl in types.OfType<RustTypeDeclaration>().Where(t => t.IsImpl))
        {
            string target = impl.Name[..impl.Name.IndexOf(" (impl", StringComparison.Ordinal)];
            int generic = target.IndexOf('<');
            if (generic >= 0) target = target[..generic];
            string scope = scopes[impl];
            if (target.StartsWith("crate::", StringComparison.Ordinal)) target = target[7..];
            else
            {
                if (target.StartsWith("self::", StringComparison.Ordinal)) target = target[6..];
                while (target.StartsWith("super::", StringComparison.Ordinal))
                {
                    target = target[7..];
                    int split = scope.LastIndexOf("::", StringComparison.Ordinal);
                    scope = split >= 0 ? scope[..split] : "";
                }
                if (scope.Length > 0) target = scope + "::" + target;
            }
            var owners = types.Where(t => t is RustTypeDeclaration { IsImpl: false }
                && t.Kind is TypeKind.Struct or TypeKind.Enum
                && scopes.TryGetValue(t, out var ownerScope)
                && (ownerScope.Length > 0 ? ownerScope + "::" + t.Name : t.Name) == target).ToList();
            if (owners.Count != 1) continue;
            owners[0].Methods.AddRange(impl.Methods);
            impl.Methods.Clear();
        }
    }

    private TypeDeclaration? TryParseTypeOrImpl(List<StatementInfo> statements, List<MethodDeclaration> freeFunctions)
    {
        int savedPos = _pos;
        var attributes = CollectPrecedingAttributes();
        bool hasDoc = HasPrecedingDocComment();

        Modifier vis = ReadVisibility();

        bool isUnsafe = MatchKeyword("unsafe");

        if (CheckKeyword("struct"))
        {
            return ParseStruct(vis, attributes, hasDoc, out var isTupleStruct, out var isUnitStruct)
                .AsRust(isTupleStruct: isTupleStruct, isUnitStruct: isUnitStruct, isUnsafe: isUnsafe);
        }
        if (CheckKeyword("enum")) return ParseEnum(vis, attributes, hasDoc).AsRust(isUnsafe: isUnsafe);
        if (CheckKeyword("type")) return ParseAlias(vis, attributes, hasDoc);
        if (CheckKeyword("trait")) return ParseTrait(vis, attributes, hasDoc, statements).AsRust(isTrait: true, isUnsafe: isUnsafe);
        if (CheckKeyword("impl"))
        {
            return ParseImpl(statements, out var isNegativeImpl)
                ?.AsRust(isImpl: true, isNegativeImpl: isNegativeImpl, isUnsafe: isUnsafe);
        }
        if (CheckIdent("union") && Peek().Kind == RustTokenKind.Identifier)
            return ParseUnion(vis, attributes, hasDoc).AsRust(isUnion: true, isUnsafe: isUnsafe);

        // A free function (possibly pub/const/async/extern/unsafe fn). Parse it here exactly
        // once with its real visibility and collect it; the main loop's fn branch only runs
        // for non-pub free fns (which never reach this method).
        if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("const")
            || CheckKeyword("extern"))
        {
            var (m, _) = ParseFn(statements);
            if (m != null)
            {
                m = m with
                {
                    Line = tokens[savedPos].Line, Column = ColAt(tokens[savedPos].Start),
                    HasDocComment = hasDoc, DocComment = DocumentationAt(tokens[savedPos].Line), Decorators = attributes
                };
                if (vis != Modifier.Private) m = m with { Modifiers = (m.Modifiers & ~Modifier.Private) | vis };
                // `unsafe` was consumed by the item-level dispatch above (for unsafe trait/impl);
                // propagate it onto a free `unsafe fn` so RustMethod.IsUnsafe is correct.
                if (isUnsafe && m is RustMethodDeclaration rm) m = rm with { IsUnsafe = true };
                freeFunctions.Add(m);
            }
            return null;
        }

        _pos = savedPos;
        return null;
    }

    private Modifier ReadVisibility()
    {
        if (!MatchKeyword("pub")) return Modifier.Private;
        if (Check("(")) { SkipParens(); return Modifier.Internal; } // pub(crate)/pub(super)/pub(in path)
        return Modifier.Public;
    }

    private void SkipWhereClause()
    {
        // A where clause runs until the item body `{` or a terminating `;` (body-less items).
        while (!IsAtEnd() && !Check("{") && !Check(";")) Advance();
    }

    private TypeDeclaration ParseUnion(Modifier vis, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance(); // skip 'union' (a contextual keyword, lexed as an identifier)
        string name = ConsumeIdentifier();
        var generics = ReadTypeParameters();
        if (CheckKeyword("where")) SkipWhereClause();
        var fields = new List<FieldDeclaration>();
        if (Check("{")) fields = ParseStructFields();
        return new TypeDeclaration(name, TypeKind.Struct, vis, [], attributes, [], [], [], [], line)
        { HasDocComment = hasDoc, Fields = fields, TypeParameters = generics, DocComment = DocumentationAt(line) };
    }

    private TypeDeclaration ParseAlias(Modifier vis, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance();
        string name = ConsumeIdentifier();
        var generics = ReadTypeParameters();
        while (!IsAtEnd() && !Check("=") && !Check(";")) Advance();
        TypeReference? target = null;
        if (Check("=")) { Advance(); target = ParseTypeReference(ConsumeType()); }
        if (Check(";")) Advance();
        return new TypeDeclaration(name, TypeKind.Alias, vis, [], attributes, [], [], [], [], line)
        { AliasTarget = target, TypeParameters = generics, HasDocComment = hasDoc, DocComment = DocumentationAt(line) }.AsRust();
    }

    private TypeDeclaration ParseStruct(
        Modifier vis,
        List<string> attributes,
        bool hasDoc,
        out bool isTupleStruct,
        out bool isUnitStruct)
    {
        isTupleStruct = false;
        isUnitStruct = false;
        int line = CurrentLine();
        Advance(); // skip 'struct'
        string name = ConsumeIdentifier();
        if (name == "") AddError(line, ColAt(Current().Start), "expected struct name after 'struct'");
        var generics = ReadTypeParameters();

        var fields = new List<FieldDeclaration>();

        if (Check("("))
        {
            isTupleStruct = true;
            // tuple struct: `struct S(T);` or `struct S(T) where T: X;`
            fields = ParseTupleFields();
            if (CheckKeyword("where")) SkipWhereClause();
            if (Check(";")) Advance();
        }
        else
        {
            if (CheckKeyword("where")) SkipWhereClause();
            if (Check(";")) { isUnitStruct = true; Advance(); } // unit struct (optionally with where)
            else if (Check("{")) { fields = ParseStructFields(); }
        }

        return new TypeDeclaration(name, TypeKind.Struct, vis, [], attributes, [], [], [], [], line)
        { HasDocComment = hasDoc, Fields = fields, DocComment = DocumentationAt(line), TypeParameters = generics };
    }

    private List<FieldDeclaration> ParseTupleFields()
    {
        var fields = new List<FieldDeclaration>();
        Advance();
        while (!IsAtEnd() && !Check(")"))
        {
            int start = _pos;
            SkipCommentsAndAttributes();
            int line = CurrentLine();
            var visibility = ReadVisibility();
            string text = ConsumeType();
            if (text.Length > 0)
                fields.Add(new FieldDeclaration(fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ParseTypeReference(text), visibility, line));
            if (Check(",")) Advance();
            if (_pos == start) Advance();
        }
        if (Check(")")) Advance();
        return fields;
    }

    private List<FieldDeclaration> ParseStructFields()
    {
        var fields = new List<FieldDeclaration>();
        Advance(); // skip {
        while (!IsAtEnd() && !Check("}"))
        {
            SkipCommentsAndAttributes();
            if (Check("}")) break;

            var vis = ReadVisibility();

            if (Check("}")) break;
            int line = CurrentLine();
            string fieldName = ConsumeIdentifier();
            if (fieldName == "" || !Check(":")) { SkipUntilCommaOrBrace(); continue; }
            Advance(); // skip :
            string fieldType = ConsumeType();
            fields.Add(new FieldDeclaration(fieldName, ParseTypeReference(fieldType), vis, line));
            if (Check(",")) Advance();
        }
        if (Check("}")) Advance();
        return fields;
    }

    private TypeDeclaration ParseEnum(Modifier vis, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance(); // skip 'enum'
        string name = ConsumeIdentifier();
        if (name == "") AddError(line, ColAt(Current().Start), "expected enum name after 'enum'");
        var generics = ReadTypeParameters();
        if (CheckKeyword("where")) SkipWhereClause();

        var modifiers = vis;
        var variants = new List<string>();
        var payloads = new List<TypeDeclaration>();

        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                int loopStart = _pos;
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                int variantStart = _pos;
                int variantLine = CurrentLine();
                string variantName = ConsumeIdentifier();
                if (variantName != "") variants.Add(variantName);
                List<FieldDeclaration>? fields = null;
                bool tuple = Check("(");
                if (tuple) fields = ParseTupleFields();
                if (Check("{")) fields = ParseStructFields();
                if (fields is not null && variantName != "")
                    payloads.Add(new TypeDeclaration(variantName, TypeKind.Struct, vis, [], [], [], [], [], [], variantLine)
                    {
                        Fields = fields, Column = ColAt(tokens[variantStart].Start),
                        EndLine = PreviousLine(), EndColumn = ColAt(tokens[Math.Max(variantStart, _pos - 1)].End)
                    }.AsRust());
                if (Check("=")) { Advance(); while (!IsAtEnd() && !Check(",") && !Check("}")) Advance(); }
                if (Check(",")) Advance();
                if (_pos == loopStart) Advance();
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Enum, modifiers, [], attributes, [], [], payloads, variants, line)
        { HasDocComment = hasDoc, DocComment = DocumentationAt(line), TypeParameters = generics };
    }

    private TypeDeclaration ParseTrait(Modifier vis, List<string> attributes, bool hasDoc, List<StatementInfo> statements)
    {
        int line = CurrentLine();
        Advance(); // skip 'trait'
        string name = ConsumeIdentifier();
        if (name == "") AddError(line, ColAt(Current().Start), "expected trait name after 'trait'");
        var generics = ReadTypeParameters();

        var baseTypes = new List<string>();
        if (Check(":"))
        {
            Advance();
            int start = _pos, depth = 0;
            while (!IsAtEnd() && !Check("{") && !CheckKeyword("where"))
            {
                if (Check("<") || Check("(")) depth++;
                if (Check(">") || Check(")")) depth--;
                if (depth == 0 && Check("+"))
                {
                    if (start < _pos && tokens[start].Kind != RustTokenKind.Lifetime)
                        baseTypes.Add(TokenText(start, _pos));
                    Advance();
                    start = _pos;
                }
                else Advance();
            }
            if (start < _pos && tokens[start].Kind != RustTokenKind.Lifetime)
                baseTypes.Add(TokenText(start, _pos));
        }
        if (CheckKeyword("where")) SkipWhereClause();

        var methods = new List<MethodDeclaration>();
        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("unsafe")
                    || CheckKeyword("const") || CheckKeyword("extern"))
                {
                    var (m, _) = ParseFn(statements);
                    // Trait items inherit the trait's visibility (a pub trait's methods are
                    // public API), so doc/visibility checks can see them.
                    if (m != null)
                    {
                        if (vis != Modifier.Private) m = m with { Modifiers = (m.Modifiers & ~Modifier.Private) | vis };
                        methods.Add(m);
                    }
                }
                else Advance();
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Interface, vis, baseTypes, attributes, [], methods, [], [], line)
        { HasDocComment = hasDoc, DocComment = DocumentationAt(line), TypeParameters = generics };
    }

    private TypeDeclaration? ParseImpl(List<StatementInfo> statements, out bool isNegativeImpl)
    {
        int line = CurrentLine();
        Advance(); // skip 'impl'
        var generics = ReadTypeParameters();

        // Read the first type/trait path (handles std::fmt::Debug, &T, [T], (A,B), dyn X, generics).
        isNegativeImpl = Check("!");
        string first = ConsumeImplTypeName();

        string typeName;
        var baseTypes = new List<string>();

        if (MatchKeyword("for"))
        {
            // impl Trait for Type
            typeName = ConsumeImplTypeName();
            if (first != "") baseTypes.Add(first);
        }
        else
        {
            typeName = first;
        }

        if (CheckKeyword("where")) SkipWhereClause();

        var methods = new List<MethodDeclaration>();
        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                int loopStart = _pos;
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                int methodStart = _pos;
                bool methodHasDoc = HasPrecedingDocComment();
                var methodAttributes = CollectPrecedingAttributes();
                var methodVisibility = ReadVisibility();
                if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("unsafe")
                    || CheckKeyword("const") || CheckKeyword("extern") || CheckKeyword("default"))
                {
                    var (m, _) = ParseFn(statements);
                    if (m != null)
                    {
                        if (methodVisibility != Modifier.Private)
                            m = m with { Modifiers = (m.Modifiers & ~Modifier.Private) | methodVisibility };
                        methods.Add(m with
                        {
                            ReceiverType = typeName, Line = tokens[methodStart].Line,
                            Column = ColAt(tokens[methodStart].Start), HasDocComment = methodHasDoc,
                            DocComment = DocumentationAt(tokens[methodStart].Line), Decorators = methodAttributes
                        });
                    }
                }
                else Advance();
                if (_pos == loopStart) Advance(); // progress guard
            }
            if (Check("}")) Advance();
        }

        if (typeName == "") typeName = "?";
        string displayName = baseTypes.Count > 0 ? $"{typeName} (impl {baseTypes[0]})" : $"{typeName} (impl)";
        return new TypeDeclaration(displayName, TypeKind.Class, Modifier.Public, baseTypes, [], [], methods, [], [], line)
        { TypeParameters = generics };
    }

    /// <summary>
    /// Consumes a type or trait reference in `impl` position and returns a single display name.
    /// Handles reference/pointer prefixes, dyn/impl, slices/arrays, tuples, qualified paths
    /// (preserving every segment), and generic arguments.
    /// </summary>
    private string ConsumeImplTypeName()
    {
        if (Check("!")) Advance();
        int start = _pos;
        int angle = 0, paren = 0, bracket = 0;
        while (!IsAtEnd())
        {
            if (angle == 0 && paren == 0 && bracket == 0
                && (Check("{") || CheckKeyword("for") || CheckKeyword("where"))) break;
            switch (Current().Value)
            {
                case "<": angle++; break;
                case ">": angle--; break;
                case "(": paren++; break;
                case ")": paren--; break;
                case "[": bracket++; break;
                case "]": bracket--; break;
            }
            Advance();
        }
        return TokenText(start, _pos);
    }

    private (MethodDeclaration?, string?) ParseFn(List<StatementInfo> statements)
    {
        int declarationStart = _pos;
        bool hasDoc = HasPrecedingDocComment();
        var decorators = CollectPrecedingAttributes();
        var modifiers = Modifier.Private;
        bool isUnsafe = false, isConst = false, isExtern = _externBlocks.Count > 0;

        // Consume fn modifiers in any order: async, const, unsafe, default, extern "C".
        while (true)
        {
            if (MatchKeyword("async")) { modifiers |= Modifier.Async; continue; }
            if (MatchKeyword("const")) { isConst = true; continue; }
            if (MatchKeyword("unsafe")) { isUnsafe = true; continue; }
            if (MatchKeyword("default")) continue;
            if (MatchKeyword("extern")) { isExtern = true; if (Current().Kind == RustTokenKind.StringLiteral) Advance(); continue; }
            break;
        }

        if (!MatchKeyword("fn")) return (null, null);

        int line = tokens[declarationStart].Line;
        string name = ConsumeIdentifier();
        if (name == "") AddError(line, ColAt(Current().Start), "expected function name after 'fn'");
        var generics = ReadTypeParameters();

        // Parameters
        var parameters = new List<ParameterDeclaration>();
        if (Check("("))
        {
            Advance();
            while (!IsAtEnd() && !Check(")"))
            {
                int loopStart = _pos;
                SkipCommentsAndAttributes();
                if (Check(")")) break;

                int parameterStart = _pos;
                int parameterLine = CurrentLine();
                while (!IsAtEnd() && !Check(":") && !Check(",") && !Check(")"))
                {
                    if (Check("(")) SkipParens();
                    else if (Check("[")) SkipBrackets();
                    else if (Check("{")) SkipBraces();
                    else Advance();
                }
                string pattern = TokenText(parameterStart, _pos);
                int patternEnd = _pos;
                if (Check(":"))
                {
                    Advance();
                    string paramType = ConsumeType();
                    if (!tokens[parameterStart..patternEnd].Any(t => t.Value == "self"))
                    {
                        string paramName = pattern.StartsWith("mut ", StringComparison.Ordinal) ? pattern[4..] : pattern;
                        if (paramName.StartsWith("ref ", StringComparison.Ordinal)) paramName = paramName[4..];
                        parameters.Add(new ParameterDeclaration(paramName, ParseTypeReference(paramType),
                            false, false, false, parameterLine)
                        {
                            Column = ColAt(tokens[parameterStart].Start),
                            EndLine = PreviousLine(),
                            EndColumn = ColAt(tokens[Math.Max(parameterStart, _pos - 1)].End)
                        });
                    }
                }
                else if (pattern == "...")
                    parameters.Add(new ParameterDeclaration("...", null, true, false, false, parameterLine));
                if (Check(",")) Advance();

                // Guaranteed forward progress: never spin on an unexpected token.
                if (_pos == loopStart) Advance();
            }
            if (Check(")")) Advance();
        }

        // Return type
        TypeReference? returnType = null;
        if (Check("->"))
        {
            Advance();
            string retType = ConsumeType();
            if (retType != "")
                returnType = ParseTypeReference(retType);
        }

        if (CheckKeyword("where")) SkipWhereClause();

        // Body
        var methodStatements = new List<StatementInfo>();
        if (Check("{"))
        {
            ParseBlock(methodStatements);
        }
        else if (Check(";")) Advance();

        statements.AddRange(methodStatements);
        var method = new MethodDeclaration(name, modifiers, decorators, returnType, parameters, line)
        {
            Statements = methodStatements, HasDocComment = hasDoc, DocComment = DocumentationAt(line),
            TypeParameters = generics, Column = ColAt(tokens[declarationStart].Start),
            EndLine = PreviousLine(), EndColumn = ColAt(tokens[Math.Max(declarationStart, _pos - 1)].End)
        }.AsRust(isUnsafe, isConst, isExtern);
        foreach (var statement in methodStatements) statement.Method = method;
        return (method, null);
    }

    private void ParseBlock(List<StatementInfo> statements)
    {
        if (!Check("{")) return;
        int start = _pos;
        SkipBraces();
        new RustBodyParser(tokens, sourceText, statements,
            token => AddError(token.Line, ColAt(token.Start), "structural analysis nesting limit (256) exceeded"),
            ParseNestedFunction, _delimiterPairs).Parse(start + 1,
            _pos > start && tokens[_pos - 1].Value == "}" ? _pos - 1 : _pos);
    }

    private int ParseNestedFunction(int start)
    {
        int saved = _pos;
        _pos = start;
        if (_functionDepth >= 128)
        {
            AddError(CurrentLine(), ColAt(Current().Start), "structural analysis nested function limit (128) exceeded");
            while (!IsAtEnd() && !Check("{") && !Check(";")) Advance();
            if (Check("{")) SkipBraces();
        }
        else
        {
            _functionDepth++;
            var (method, _) = ParseFn(_nestedStatements);
            _functionDepth--;
            if (method is not null) _nestedFunctions.Add((method, _scope));
        }
        int end = _pos;
        _pos = saved;
        return end;
    }

    // Helper: parse a use statement
    private void ParseUse(List<string> usings)
    {
        ParseUseTree("", usings, 0);
        if (Check(";")) Advance();
    }

    private void ParseUseTree(string prefix, List<string> usings, int depth)
    {
        if (depth > 128)
        {
            AddError(CurrentLine(), ColAt(Current().Start), "use-tree nesting limit (128) exceeded");
            if (Check("{")) SkipBraces();
            return;
        }
        int start = _pos;
        string? alias = null;
        string path = prefix;
        while (!IsAtEnd() && !Check(";") && !Check(",") && !Check("}"))
        {
            if (Check("{"))
            {
                Advance();
                while (!IsAtEnd() && !Check("}"))
                {
                    int before = _pos;
                    ParseUseTree(path, usings, depth + 1);
                    if (Check(",")) Advance();
                    if (_pos == before) Advance();
                }
                if (Check("}")) Advance();
                return;
            }
            if (MatchKeyword("as"))
            {
                if (!IsAtEnd()) { alias = Current().Value; Advance(); }
                break;
            }
            if (!Check("::"))
            {
                string part = Current().Value;
                if (part != "self" || path.Length == 0)
                    path = path.Length == 0 ? part : path + "::" + part;
            }
            Advance();
        }
        if (path.Length > 0)
        {
            usings.Add(path);
            int split = path.LastIndexOf("::", StringComparison.Ordinal);
            string module = split >= 0 ? path[..split] : path;
            string? name = split >= 0 ? path[(split + 2)..] : null;
            _imports.Add(new ImportInfo(module, tokens[start].Line)
            {
                Name = name == "*" ? null : name, Alias = alias, IsWildcard = name == "*",
                Column = ColAt(tokens[start].Start), EndLine = PreviousLine(),
                EndColumn = ColAt(tokens[Math.Max(start, _pos - 1)].End)
            });
        }
    }

    #region Token helpers

    private RustToken Current() => _pos < tokens.Count ? tokens[_pos] : tokens[^1];
    private RustToken Peek() => _pos + 1 < tokens.Count ? tokens[_pos + 1] : tokens[^1];
    private int CurrentLine() => Current().Line;
    private int PreviousLine() => _pos > 0 ? tokens[_pos - 1].Line : 1;
    private bool IsAtEnd() => _pos >= tokens.Count || Current().Kind == RustTokenKind.Eof;
    private void Advance() { if (!IsAtEnd()) _pos++; }

    private bool Check(string value) => !IsAtEnd() && Current().Value == value;
    private bool CheckKeyword(string kw) => !IsAtEnd() && Current().Kind == RustTokenKind.Keyword && Current().Value == kw;
    private bool CheckIdent(string id) => !IsAtEnd() && Current().Kind == RustTokenKind.Identifier && Current().Value == id;
    private bool MatchKeyword(string kw) { if (CheckKeyword(kw)) { Advance(); return true; } return false; }

    // True if the item at the current position is preceded by a #[cfg(test)] attribute.
    private bool PrecededByCfgTest()
    {
        for (int i = _pos - 1; i >= 0 && i >= _pos - 8; i--)
        {
            var t = tokens[i];
            if (t.Kind == RustTokenKind.Attribute)
            {
                var v = t.Value.Replace(" ", "");
                if (v.Contains("cfg(test)") || v.Contains("cfg(all(test") || v.Contains(",test)") || v.Contains("(test,")) return true;
            }
            else if (t.Kind is not RustTokenKind.DocComment and not RustTokenKind.LineComment and not RustTokenKind.BlockComment)
            {
                break;
            }
        }
        return false;
    }

    // Skips `mod name { ... }` or `mod name;`.
    private void SkipModuleBody()
    {
        Advance();           // mod
        ConsumeIdentifier(); // name
        if (Check("{")) SkipBraces();
        else if (Check(";")) Advance();
    }

    private string ConsumeIdentifier()
    {
        if (!IsAtEnd() && Current().Kind == RustTokenKind.Identifier)
        { var v = Current().Value; Advance(); return v; }
        if (!IsAtEnd() && Current().Kind == RustTokenKind.Keyword && Current().Value == "Self")
        { Advance(); return "Self"; }
        return "";
    }

    private TypeReference ParseTypeReference(string text) => RustTypeReferences.Parse(text,
        () => AddError(CurrentLine(), ColAt(Current().Start), "type nesting limit (128) exceeded"));

    private string ConsumeType()
    {
        int start = _pos;
        int angle = 0, paren = 0, bracket = 0;
        while (!IsAtEnd())
        {
            if (Check("<")) { angle++; Advance(); continue; }
            if (Check(">")) { if (angle == 0) break; angle--; Advance(); continue; }
            if (Check("(")) { paren++; Advance(); continue; }
            if (Check(")")) { if (paren == 0) break; paren--; Advance(); continue; }
            if (Check("[")) { bracket++; Advance(); continue; }
            if (Check("]")) { if (bracket == 0) break; bracket--; Advance(); continue; }
            if (angle == 0 && paren == 0 && bracket == 0
                && (Check(",") || Check(";") || Check("{") || Check("}") || CheckKeyword("where"))) break;
            Advance();
        }
        if (_pos == start) return "";
        return TokenText(start, _pos);
    }

    private void SkipGenerics()
    {
        if (!Check("<")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("<")) { depth++; Advance(); }
            else if (Check(">")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }

    }

    private List<string> ReadTypeParameters()
    {
        if (!Check("<")) return [];
        int start = _pos;
        SkipGenerics();
        int end = _pos - 1;
        var names = new List<string>();
        int depth = 0;
        bool parameter = true;
        for (int i = start + 1; i < end; i++)
        {
            var token = tokens[i];
            if (token.Value is "<" or "(" or "[" or "{") { depth++; continue; }
            if (token.Value is ">" or ")" or "]" or "}") { depth--; continue; }
            if (depth != 0) continue;
            if (token.Value == ",") { parameter = true; continue; }
            if (parameter && (token.Kind is RustTokenKind.Identifier or RustTokenKind.Lifetime))
            { names.Add(token.Value); parameter = false; }
        }
        return names;
    }

    private void SkipParens()
    {
        if (!Check("(")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("(")) { depth++; Advance(); }
            else if (Check(")")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }
    }

    private void SkipBraces()
    {
        if (!Check("{")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("{")) { depth++; Advance(); }
            else if (Check("}")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }
    }

    private void SkipBrackets()
    {
        if (!Check("[")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("[")) { depth++; Advance(); }
            else if (Check("]")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }
    }

    private void SkipUntil(string value)
    {
        while (!IsAtEnd() && !Check(value)) Advance();
    }

    private void SkipUntilCommaOrBrace()
    {
        while (!IsAtEnd() && !Check(",") && !Check("}")) Advance();
        if (Check(",")) Advance();
    }

    private void SkipUntilCommaOrParen()
    {
        while (!IsAtEnd() && !Check(",") && !Check(")")) Advance();
    }

    private void SkipCommentsAndAttributes()
    {
        while (!IsAtEnd() && Current().Kind is RustTokenKind.LineComment or RustTokenKind.DocComment or RustTokenKind.BlockComment or RustTokenKind.Attribute)
            Advance();
    }

    private bool IsFnAhead()
    {
        for (int i = _pos; i < Math.Min(_pos + 5, tokens.Count); i++)
            if (tokens[i].Kind == RustTokenKind.Keyword && tokens[i].Value == "fn") return true;
        return false;
    }

    private bool HasPrecedingDocComment()
    {
        for (int i = _pos - 1; i >= 0 && i >= _pos - 10; i--)
        {
            if (tokens[i].Kind == RustTokenKind.DocComment) return true;
            if (tokens[i].Kind is RustTokenKind.Attribute or RustTokenKind.LineComment or RustTokenKind.BlockComment) continue;
            // Skip visibility/modifier keywords consumed by the caller (e.g., pub, async, unsafe)
            if (tokens[i].Kind == RustTokenKind.Keyword && tokens[i].Value is "pub" or "async" or "unsafe") continue;
            // Skip pub(crate) parenthesized visibility
            if (tokens[i].Kind == RustTokenKind.Punctuation && tokens[i].Value is ")" or "(" or "crate") continue;
            break;
        }
        return false;
    }

    private List<string> CollectPrecedingAttributes()
    {
        var attrs = new List<string>();
        for (int i = _pos - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == RustTokenKind.Attribute)
                attrs.Insert(0, tokens[i].Value.Trim('#', '[', ']'));
            else if (tokens[i].Kind is RustTokenKind.DocComment or RustTokenKind.LineComment or RustTokenKind.BlockComment)
                continue;
            else break;
        }
        return attrs;
    }

    private HashSet<int> ExtractCommentLines()
    {
        var lines = new HashSet<int>();
        foreach (var t in tokens)
            if (t.Kind is RustTokenKind.LineComment or RustTokenKind.DocComment or RustTokenKind.BlockComment)
                for (int line = t.Line; line <= t.Line + t.Value.Count(c => c == '\n'); line++)
                    lines.Add(line);
        return lines;
    }

    private string TokenText(int start, int end) =>
        start < end ? sourceText[tokens[start].Start..tokens[end - 1].End] : "";

    private string? DocumentationAt(int line)
    {
        int index = tokens.FindIndex(t => t.Line == line && t.Kind != RustTokenKind.DocComment);
        if (index < 0) return null;
        var docs = new List<string>();
        for (int i = index - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.Kind == RustTokenKind.DocComment) docs.Insert(0, token.Value);
            else if (token.Kind is RustTokenKind.Attribute or RustTokenKind.LineComment or RustTokenKind.BlockComment
                || token.Value is "pub" or "(" or ")" or "crate" or "unsafe" or "async" or "const" or "extern"
                || token.Kind == RustTokenKind.StringLiteral) continue;
            else break;
        }
        return docs.Count > 0 ? string.Join("\n", docs) : null;
    }

    #endregion
}

#endregion
