using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class GoSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".go"];
    public override string Language => "go";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lexer = new GoLexer(sourceText);
        var tokens = lexer.Tokenize();
        var parser = new GoParser(tokens, sourceText, filePath, lexer.LexErrors);
        return parser.Parse(filePath);
    }
}

#region Lexer

internal enum GoTokenKind
{
    Identifier, Keyword, Punctuation, StringLiteral, NumberLiteral,
    LineComment, BlockComment, Eof
}

// Col is 1-based column of the first character of the token on its line.
internal record struct GoToken(GoTokenKind Kind, string Value, int Line, int Col, int Start, int End);

internal class GoLexer(string source)
{
    private int _pos;
    private int _line = 1;
    private int _lineStart = 0; // character offset of the start of the current line

    private readonly List<(int Line, int Col, string Message)> _lexErrors = [];

    /// <summary>Lex-phase errors (unterminated strings / block comments).</summary>
    public IReadOnlyList<(int Line, int Col, string Message)> LexErrors => _lexErrors;

    private static readonly HashSet<string> Keywords =
    [
        "break", "case", "chan", "const", "continue", "default", "defer",
        "else", "fallthrough", "for", "func", "go", "goto", "if", "import",
        "interface", "map", "package", "range", "return", "select", "struct",
        "switch", "type", "var"
    ];

    private int ColAt(int pos) => pos - _lineStart + 1;

    public List<GoToken> Tokenize()
    {
        var tokens = new List<GoToken>();
        while (_pos < source.Length)
        {
            SkipWhitespace();
            if (_pos >= source.Length) break;

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

        tokens.Add(new GoToken(GoTokenKind.Eof, "", _line, ColAt(_pos), _pos, _pos));
        return InsertSemicolons(tokens);
    }

    /// <summary>
    /// Applies Go's automatic semicolon insertion (ASI) rule post-tokenization.
    /// A virtual semicolon is inserted after the last token on a line when that token is
    /// an identifier, literal, break/continue/fallthrough/return, or one of ++, --, ), ], }.
    /// </summary>
    private static List<GoToken> InsertSemicolons(List<GoToken> raw)
    {
        var result = new List<GoToken>(raw.Count + 32);
        for (int i = 0; i < raw.Count; i++)
        {
            var cur = raw[i];
            result.Add(cur);

            // Comments are transparent to ASI — skip them when finding the next real token.
            if (cur.Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment) continue;

            if (!NeedsAutoSemicolon(cur)) continue;

            // Find the next non-comment token to check if it's on a later line.
            int j = i + 1;
            while (j < raw.Count && raw[j].Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment)
                j++;

            bool nextIsLaterLine = j >= raw.Count || raw[j].Line > cur.Line;
            if (nextIsLaterLine)
                result.Add(new GoToken(GoTokenKind.Punctuation, ";", cur.Line, cur.Col + cur.Value.Length, cur.End, cur.End));
        }
        return result;
    }

    private static bool NeedsAutoSemicolon(GoToken t)
    {
        if (t.Kind is GoTokenKind.Identifier or GoTokenKind.StringLiteral or GoTokenKind.NumberLiteral)
            return true;
        if (t.Kind == GoTokenKind.Keyword && t.Value is "break" or "continue" or "fallthrough" or "return")
            return true;
        if (t.Kind == GoTokenKind.Punctuation && t.Value is "++" or "--" or ")" or "]" or "}")
            return true;
        return false;
    }

    private void SkipWhitespace()
    {
        while (_pos < source.Length)
        {
            char c = source[_pos];
            if (c == '\n') { _pos++; _line++; _lineStart = _pos; }
            else if (c is ' ' or '\t' or '\r') _pos++;
            else break;
        }
    }

    private GoToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        while (_pos < source.Length && source[_pos] != '\n') _pos++;
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
            else _pos++;
        }
        if (!closed)
            _lexErrors.Add((line, col, "unterminated block comment"));
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
            if (c == '\n') break; // Go interpreted strings must not span lines
            if (c == '\\') { _pos++; if (_pos < source.Length) _pos++; }
            else _pos++;
        }
        if (!closed)
            _lexErrors.Add((line, col, "unterminated string literal"));
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
            else _pos++;
        }
        if (!closed)
            _lexErrors.Add((line, col, "unterminated raw string literal"));
        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadRune()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        _pos++; // skip '
        if (_pos < source.Length && source[_pos] == '\\') { _pos++; if (_pos < source.Length) _pos++; }
        else if (_pos < source.Length) _pos++;
        if (_pos < source.Length && source[_pos] == '\'') _pos++;
        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '.' or '_' or 'x' or 'X'))
            _pos++;
        return new GoToken(GoTokenKind.NumberLiteral, source[start.._pos], line, col, start, _pos);
    }

    private GoToken ReadIdentifierOrKeyword()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            _pos++;
        var value = source[start.._pos];
        var kind = Keywords.Contains(value) ? GoTokenKind.Keyword : GoTokenKind.Identifier;
        return new GoToken(kind, value, line, col, start, _pos);
    }

    private GoToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        int col = ColAt(start);
        if (_pos + 2 < source.Length)
        {
            var three = source.Substring(_pos, 3);
            if (three is "..." or "<<=" or ">>=")
            { _pos += 3; return new GoToken(GoTokenKind.Punctuation, three, line, col, start, _pos); }
        }
        if (_pos + 1 < source.Length)
        {
            var two = source.Substring(_pos, 2);
            if (two is ":=" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<-" or "++" or "--" or "+=" or "-=" or "*=" or "/=" or "<<" or ">>")
            { _pos += 2; return new GoToken(GoTokenKind.Punctuation, two, line, col, start, _pos); }
        }
        _pos++;
        return new GoToken(GoTokenKind.Punctuation, source[start.._pos], line, col, start, _pos);
    }
}

#endregion

#region Parser

internal class GoParser(List<GoToken> tokens, string sourceText, string filePath,
    IReadOnlyList<(int Line, int Col, string Message)> lexErrors)
{
    private int _pos;
    private readonly List<string> _errors = [];

    private void AddError(string message, int line, int col) =>
        _errors.Add($"{filePath}({line},{col}): error: {message}");

    public SourceFile Parse(string filePath)
    {
        // Seed errors from the lex phase (unterminated strings / block comments)
        foreach (var (line, col, msg) in lexErrors)
            AddError(msg, line, col);

        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var receiverMethods = new List<(string Receiver, MethodDeclaration Method)>();
        var freeFunctions = new List<MethodDeclaration>();
        var usings = new List<string>();
        string? ns = null;

        while (!IsAtEnd())
        {
            SkipComments();
            if (IsAtEnd()) break;

            if (MatchKeyword("package"))
            {
                ns = ConsumeIdentifier();
            }
            else if (CheckKeyword("import"))
            {
                ParseImports(usings);
            }
            else if (CheckKeyword("type"))
            {
                ParseTypeDecl(types, statements);
            }
            else if (CheckKeyword("func"))
            {
                var method = ParseFunc(statements);
                if (method is { } parsed && parsed.method != null)
                {
                    if (parsed.receiver != null)
                        receiverMethods.Add((parsed.receiver, parsed.method));
                    else
                        freeFunctions.Add(parsed.method);
                }
            }
            else if (CheckKeyword("var") || CheckKeyword("const"))
            {
                SkipVarOrConst();
            }
            else
            {
                Advance();
            }
        }

        foreach (var (receiver, method) in receiverMethods)
        {
            var type = types.FirstOrDefault(t => t.Name == receiver);
            type?.Methods.Add(method);
        }

        // Go free functions (no receiver) aren't members of any type. Surface them as methods of a
        // synthetic per-file "(functions)" type (mirrors the Rust provider) so they appear in the
        // flat Methods collection and can be narrowed with :asGo.
        if (freeFunctions.Count > 0)
        {
            var moduleName = System.IO.Path.GetFileNameWithoutExtension(filePath) + " (functions)";
            types.Add(new TypeDeclaration(moduleName, TypeKind.Class, Modifier.Public, [], [], [], freeFunctions, [], [], 0).AsGo());
        }

        // Detect missing package clause — every non-empty Go file must declare one.
        if (ns == null && HasSubstantialContent())
            AddError("missing package clause", 1, 1);

        // Check bracket balance only when no lex errors (lex errors truncate the token stream).
        if (lexErrors.Count == 0)
            CheckBracketBalance();

        return new SourceFile(filePath, "go", types, statements, sourceText)
        {
            Namespace = ns,
            Usings = usings,
            Regions = [],
            CommentLines = ExtractCommentLines(),
            ParseErrors = [.. _errors]
        };
    }

    /// <summary>True when the file has tokens beyond comments and EOF (real code present).</summary>
    private bool HasSubstantialContent() =>
        tokens.Any(t => t.Kind is not GoTokenKind.LineComment
                                  and not GoTokenKind.BlockComment
                                  and not GoTokenKind.Eof);

    /// <summary>
    /// Scans all tokens for bracket balance. Reports mismatched or unclosed brackets.
    /// Skips string literals (their content has already been consumed by the lexer).
    /// </summary>
    private void CheckBracketBalance()
    {
        int braces = 0, parens = 0, brackets = 0;
        foreach (var t in tokens)
        {
            if (t.Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment
                       or GoTokenKind.StringLiteral or GoTokenKind.NumberLiteral
                       or GoTokenKind.Eof) continue;
            switch (t.Value)
            {
                case "{": braces++; break;
                case "}":
                    if (--braces < 0) { AddError("unexpected '}'", t.Line, t.Col); braces = 0; }
                    break;
                case "(": parens++; break;
                case ")":
                    if (--parens < 0) { AddError("unexpected ')'", t.Line, t.Col); parens = 0; }
                    break;
                case "[": brackets++; break;
                case "]":
                    if (--brackets < 0) { AddError("unexpected ']'", t.Line, t.Col); brackets = 0; }
                    break;
            }
        }
        var eof = tokens[^1];
        if (braces > 0) AddError("expected closing '}'", eof.Line, eof.Col);
        if (parens > 0) AddError("expected closing ')'", eof.Line, eof.Col);
        if (brackets > 0) AddError("expected closing ']'", eof.Line, eof.Col);
    }

    private void ParseImports(List<string> usings)
    {
        Advance(); // skip 'import'
        if (Check("("))
        {
            Advance();
            while (!IsAtEnd() && !Check(")"))
            {
                SkipComments();
                if (Check(")")) break;
                // optional alias
                if (Current().Kind == GoTokenKind.Identifier || Check(".") || Check("_"))
                    Advance();
                if (Current().Kind == GoTokenKind.StringLiteral)
                {
                    usings.Add(Current().Value.Trim('"'));
                    Advance();
                }
                else Advance();
            }
            if (Check(")")) Advance();
        }
        else if (Current().Kind == GoTokenKind.StringLiteral)
        {
            usings.Add(Current().Value.Trim('"'));
            Advance();
        }
    }

    private void ParseTypeDecl(List<TypeDeclaration> types, List<StatementInfo> statements)
    {
        Advance(); // skip 'type'

        if (Check("("))
        {
            // type ( ... )
            Advance();
            while (!IsAtEnd() && !Check(")"))
            {
                SkipComments();
                if (Check(")")) break;
                var t = ParseSingleType(statements);
                if (t != null) types.Add(t);
            }
            if (Check(")")) Advance();
        }
        else
        {
            var t = ParseSingleType(statements);
            if (t != null) types.Add(t);
        }
    }

    private TypeDeclaration? ParseSingleType(List<StatementInfo> statements)
    {
        SkipComments();
        bool hasDoc = HasPrecedingDocComment();

        if (Current().Kind != GoTokenKind.Identifier)
        {
            if (!IsAtEnd())
                AddError($"expected type name, got '{Current().Value}'", Current().Line, Current().Col);
            return null;
        }

        string name = ConsumeIdentifier();
        if (name == "") return null;
        int line = CurrentLine();

        bool isExported = char.IsUpper(name[0]);
        var modifiers = isExported ? Modifier.Public : Modifier.Private;

        // Skip optional type parameters [T any] so we can still see 'struct'/'interface'
        SkipGenerics();

        if (CheckKeyword("struct"))
        {
            return ParseStructType(name, modifiers, hasDoc, line);
        }
        else if (CheckKeyword("interface"))
        {
            return ParseInterfaceType(name, modifiers, hasDoc, line, statements);
        }
        else
        {
            // type alias or other
            bool isAlias = Check("=");
            string aliasType = ConsumeType();
            return new TypeDeclaration(name, TypeKind.Struct, modifiers, [], [], [], [], [], [], line)
            { HasDocComment = hasDoc }.AsGo(isStruct: true, isTypeAlias: isAlias);
        }
    }

    private TypeDeclaration ParseStructType(string name, Modifier modifiers, bool hasDoc, int line)
    {
        Advance(); // skip 'struct'
        var fields = new List<FieldDeclaration>();
        var embedded = new List<string>();
        bool hasStructTags = false;

        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipComments();
                if (Check("}")) break;

                // Could be an embedded type or field(s)
                string fieldName = "";
                int fieldLine = CurrentLine();

                if (Current().Kind == GoTokenKind.Identifier)
                {
                    string first = Current().Value;
                    Advance();

                    if (Current().Kind == GoTokenKind.Identifier || Check("*") || Check("[") || CheckKeyword("map") || CheckKeyword("func") || CheckKeyword("chan") || CheckKeyword("interface") || CheckKeyword("struct"))
                    {
                        // field Name Type — use includeBraces so interface{} / struct{} field types work
                        fieldName = first;
                        string fieldType = ConsumeType(includeBraces: true);
                        bool fieldExported = char.IsUpper(fieldName[0]);
                        fields.Add(new FieldDeclaration(fieldName,
                            new TypeReference(fieldType, null, [], fieldType),
                            fieldExported ? Modifier.Public : Modifier.Private, fieldLine));
                    }
                    else
                    {
                        // embedded type
                        embedded.Add(first);
                    }
                }
                else if (Check("*"))
                {
                    Advance();
                    if (Current().Kind == GoTokenKind.Identifier)
                    {
                        embedded.Add("*" + Current().Value);
                        Advance();
                    }
                }
                else if (Check(";"))
                {
                    Advance(); // skip ASI semicolons between fields
                }
                else Advance();

                // Skip struct tags
                if (Current().Kind == GoTokenKind.StringLiteral)
                {
                    hasStructTags = true;
                    Advance();
                }
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Struct, modifiers, embedded, [], [], [], [], [], line)
        { HasDocComment = hasDoc, Fields = fields }.AsGo(isStruct: true, hasStructTags: hasStructTags);
    }

    private TypeDeclaration ParseInterfaceType(string name, Modifier modifiers, bool hasDoc, int line, List<StatementInfo> statements)
    {
        Advance(); // skip 'interface'
        var methods = new List<MethodDeclaration>();
        var embedded = new List<string>();
        bool hasUnion = false;
        bool hasUnderlying = false;

        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipComments();
                if (Check("}")) break;
                if (Check("|")) hasUnion = true;
                if (Check("~")) hasUnderlying = true;

                if (Current().Kind == GoTokenKind.Identifier)
                {
                    string methodOrType = Current().Value;
                    int mLine = CurrentLine();
                    Advance();

                    if (Check("("))
                    {
                        // Method signature
                        var parameters = ParseParamList();
                        TypeReference? returnType = ParseReturnType(out bool hasNamedReturns);
                        bool exported = char.IsUpper(methodOrType[0]);
                        methods.Add(new MethodDeclaration(methodOrType,
                            exported ? Modifier.Public : Modifier.Private, [], returnType, parameters, mLine)
                            .AsGo(
                                hasNamedReturns: hasNamedReturns,
                                isVariadic: parameters.Any(p => p.IsVariadic)));
                    }
                    else
                    {
                        // Embedded interface
                        embedded.Add(methodOrType);
                    }
                }
                else Advance();
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Interface, modifiers, embedded, [], [], methods, [], [], line)
        { HasDocComment = hasDoc }.AsGo(isInterface: true, hasUnionTypeSet: hasUnion, hasUnderlyingTypeTerms: hasUnderlying);
    }

    private (MethodDeclaration? method, string? receiver)? ParseFunc(List<StatementInfo> statements)
    {
        bool hasDoc = HasPrecedingDocComment();
        Advance(); // skip 'func'

        string? receiver = null;
        string? receiverType = null;
        bool isPointerReceiver = false;

        // Method receiver: func (r *Type) Name(...)
        if (Check("("))
        {
            Advance();
            if (Current().Kind == GoTokenKind.Identifier)
            {
                receiver = Current().Value;
                Advance();
            }
            if (Check("*"))
            {
                isPointerReceiver = true;
                Advance();
            }
            if (Current().Kind == GoTokenKind.Identifier)
            {
                receiverType = Current().Value;
                Advance();
            }
            SkipGenerics();
            if (Check(")")) Advance();
        }

        if (Current().Kind != GoTokenKind.Identifier)
        {
            // func keyword not followed by a name — report the error and skip the body
            if (!IsAtEnd())
                AddError($"expected function name, got '{Current().Value}'", Current().Line, Current().Col);
            SkipBraces();
            return null;
        }

        string name = ConsumeIdentifier();
        if (name == "") { SkipBraces(); return null; }
        int line = CurrentLine();
        bool isGeneric = Check("[");
        SkipGenerics();

        var parameters = ParseParamList();
        TypeReference? returnType = ParseReturnType(out bool hasNamedReturns);

        bool isExported = char.IsUpper(name[0]);
        var modifiers = isExported ? Modifier.Public : Modifier.Private;

        var methodStatements = new List<StatementInfo>();
        if (Check("{"))
        {
            ParseBlock(methodStatements);
        }
        statements.AddRange(methodStatements);

        var method = new MethodDeclaration(name, modifiers, [], returnType, parameters, line)
        { Statements = methodStatements, HasDocComment = hasDoc }
            .AsGo(
                isPointerReceiver: isPointerReceiver,
                hasNamedReturns: hasNamedReturns,
                isVariadic: parameters.Any(p => p.IsVariadic),
                isGeneric: isGeneric);

        return (method, receiverType);
    }

    private List<ParameterDeclaration> ParseParamList()
    {
        var parameters = new List<ParameterDeclaration>();
        if (!Check("(")) return parameters;
        Advance();
        var names = new List<string>();
        while (!IsAtEnd() && !Check(")"))
        {
            SkipComments();
            if (Check(")")) break;

            if (Check("..."))
            {
                Advance();
                string varType = ConsumeType(includeBraces: true);
                string pName = names.Count > 0 ? names[^1] : "args";
                if (names.Count > 0) names.RemoveAt(names.Count - 1);
                parameters.Add(new ParameterDeclaration(pName, new TypeReference(varType, null, [], varType), true, false, false, 0));
                if (Check(",")) Advance();
                continue;
            }

            if (Current().Kind == GoTokenKind.Identifier)
            {
                string first = Current().Value;
                Advance();

                if (Check(",") || Check(")"))
                {
                    // Could be just a type name with no param name
                    names.Add(first);
                    if (Check(",")) Advance();
                }
                else if (Current().Kind == GoTokenKind.Identifier || Check("*") || Check("[") || CheckKeyword("map") || CheckKeyword("func") || CheckKeyword("chan") || CheckKeyword("interface") || CheckKeyword("struct") || Check("..."))
                {
                    // first is param name, now consume type
                    names.Add(first);
                    bool isVariadic = Check("...");
                    if (isVariadic) Advance();
                    string paramType = ConsumeType(includeBraces: true);
                    foreach (var n in names)
                        parameters.Add(new ParameterDeclaration(n, new TypeReference(paramType, null, [], paramType), isVariadic, false, false, 0));
                    names.Clear();
                    if (Check(",")) Advance();
                }
                else
                {
                    names.Add(first);
                    if (Check(",")) Advance();
                }
            }
            else
            {
                // type without name (e.g. *Type, []byte, interface{}, etc.)
                string paramType = ConsumeType(includeBraces: true);
                foreach (var n in names)
                    parameters.Add(new ParameterDeclaration(n, new TypeReference(paramType, null, [], paramType), false, false, false, 0));
                names.Clear();
                if (names.Count == 0 && paramType != "")
                    parameters.Add(new ParameterDeclaration("", new TypeReference(paramType, null, [], paramType), false, false, false, 0));
                // Guard against infinite loop: if ConsumeType consumed nothing and we're not at ),
                // force advance to prevent getting stuck.
                else if (paramType == "" && !Check(")")) Advance();
                if (Check(",")) Advance();
            }
        }
        // If we have leftover names, they're types
        foreach (var n in names)
            parameters.Add(new ParameterDeclaration("", new TypeReference(n, null, [], n), false, false, false, 0));
        if (Check(")")) Advance();
        return parameters;
    }

    private TypeReference? ParseReturnType(out bool hasNamedReturns)
    {
        hasNamedReturns = false;
        if (Check("("))
        {
            // Multiple returns
            int start = _pos;
            SkipParens();
            string multi = string.Join("", tokens[start.._pos].Select(t => t.Value));
            hasNamedReturns = HasNamedReturnParameters(tokens[start.._pos]);
            return new TypeReference(multi, null, [], multi);
        }
        if (!Check("{") && !IsAtEnd() && Current().Kind is GoTokenKind.Identifier or GoTokenKind.Keyword && !CheckKeyword("func"))
        {
            if (Check("*")) { Advance(); }
            string retType = ConsumeType();
            if (retType != "") return new TypeReference(retType, null, [], retType);
        }
        return null;
    }

    private static bool HasNamedReturnParameters(IReadOnlyList<GoToken> returnTokens)
    {
        var group = new List<GoToken>();
        int depth = 0;
        foreach (var token in returnTokens)
        {
            if (token.Value is "(" or "[" or "{") depth++;
            else if (token.Value is ")" or "]" or "}") depth--;

            if (depth == 1 && token.Value == ",")
            {
                if (IsNamedReturnGroup(group)) return true;
                group.Clear();
                continue;
            }

            if (depth == 1 && token.Value is not "(" and not ")")
                group.Add(token);
        }

        return IsNamedReturnGroup(group);
    }

    private static bool IsNamedReturnGroup(IReadOnlyList<GoToken> group)
    {
        var significant = group
            .Where(t => t.Kind is GoTokenKind.Identifier or GoTokenKind.Keyword || t.Value is "*" or "...")
            .Take(2)
            .ToList();
        if (significant.Count < 2 || significant[0].Kind != GoTokenKind.Identifier)
            return false;

        return significant[1].Kind is GoTokenKind.Identifier or GoTokenKind.Keyword
            || significant[1].Value is "*" or "...";
    }

    private void ParseBlock(List<StatementInfo> statements)
    {
        if (!Check("{")) return;
        Advance();
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            if (Check("{")) { depth++; Advance(); continue; }
            if (Check("}")) { depth--; if (depth == 0) { Advance(); break; } Advance(); continue; }

            if (CheckKeyword("defer"))
            {
                statements.Add(new GoStatementInfo("defer", [], null, null, [], CurrentLine(), true));
                Advance();
                continue;
            }

            if (CheckKeyword("go"))
            {
                statements.Add(new GoStatementInfo("go", [], null, null, [], CurrentLine(), true));
                Advance();
                continue;
            }

            if (CheckKeyword("select"))
            {
                statements.Add(new GoStatementInfo("select", [], null, null, [], CurrentLine(), true));
                Advance();
                continue;
            }

            if (CheckKeyword("for"))
            {
                int stmtLine = CurrentLine();
                if (ContainsKeywordBeforeBlock("range"))
                    statements.Add(new GoStatementInfo("range", [], null, null, [], stmtLine, true));
                else
                    statements.Add(new GoStatementInfo("for", [], null, null, [], stmtLine, true));
                Advance();
                continue;
            }

            if (CheckKeyword("switch"))
            {
                int stmtLine = CurrentLine();
                if (ContainsKeywordBeforeBlock("type"))
                    statements.Add(new GoStatementInfo("type-switch", [], null, null, [], stmtLine, true));
                else
                    statements.Add(new GoStatementInfo("switch", [], null, null, [], stmtLine, true));
                Advance();
                continue;
            }

            if (CheckKeyword("if"))
            {
                int stmtLine = CurrentLine();
                bool isErrHandler = ContainsErrNilBeforeBlock();
                statements.Add(new GoStatementInfo("if", [], null, null, [], stmtLine, true)
                {
                    IsErrorHandler = isErrHandler,
                    IsGenericErrorHandler = false
                });
                Advance();
                continue;
            }

            if (CheckKeyword("return"))
            {
                statements.Add(new GoStatementInfo("return", [], null, null, [], CurrentLine(), true));
                Advance();
                continue;
            }

            // Detect panic/recover
            if (Current().Kind == GoTokenKind.Identifier && Current().Value is "panic")
            {
                int stmtLine = CurrentLine();
                Advance();
                if (Check("(")) SkipParens();
                statements.Add(new GoStatementInfo("throw", [], null, "panic", [], stmtLine, true));
                continue;
            }
            if (Current().Kind == GoTokenKind.Identifier && Current().Value is "recover")
            {
                int stmtLine = CurrentLine();
                Advance();
                if (Check("(")) SkipParens();
                // recover() catches all panics — it is an unconditional (generic) error handler.
                statements.Add(new GoStatementInfo("catch", [], null, "recover", [], stmtLine, true)
                { IsErrorHandler = true, IsGenericErrorHandler = true });
                continue;
            }

            // Detect function calls
            if (Current().Kind == GoTokenKind.Identifier)
            {
                int stmtLine = CurrentLine();
                string memberName = Current().Value;
                string typeName = "";
                Advance();

                // package.Func or type.Method
                while (Check(".") && Peek().Kind == GoTokenKind.Identifier)
                {
                    Advance(); // skip .
                    typeName = typeName == "" ? memberName : typeName + "." + memberName;
                    memberName = Current().Value;
                    Advance();
                }

                if (Check("("))
                {
                    statements.Add(new GoStatementInfo("call", [],
                        typeName != "" ? typeName : null, memberName, [], stmtLine, true));
                    SkipParens();
                }
                continue;
            }

            Advance();
        }
    }

    /// <summary>
    /// Looks ahead from the current 'if' keyword to detect the Go error-handling idiom
    /// <c>if err != nil { … }</c> without consuming any tokens.
    /// </summary>
    private bool ContainsErrNilBeforeBlock()
    {
        bool sawErr = false;
        bool sawNe = false;
        int depth = 0;
        for (int i = _pos + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == GoTokenKind.Eof) break;
            if (depth == 0 && t.Value == "{") break;
            if (t.Value is "(" or "[") depth++;
            if (t.Value is ")" or "]") { if (depth > 0) depth--; }
            if (t.Kind == GoTokenKind.Identifier && t.Value == "err") sawErr = true;
            if (t.Value == "!=" && sawErr) sawNe = true;
            if (t.Kind == GoTokenKind.Identifier && t.Value == "nil" && sawNe) return true;
        }
        return false;
    }

    private bool ContainsKeywordBeforeBlock(string keyword)
    {
        int depth = 0;
        for (int i = _pos; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 && token.Value == "{") return false;
            if (token.Value is "(" or "[" or "{") depth++;
            else if (token.Value is ")" or "]" or "}") depth--;
            if (depth <= 1 && token.Kind == GoTokenKind.Keyword && token.Value == keyword) return true;
            if (depth == 0 && token.Value == ";") return false;
        }
        return false;
    }

    private void SkipVarOrConst()
    {
        Advance(); // skip var/const
        if (Check("("))
        {
            Advance();
            int depth = 1;
            while (!IsAtEnd() && depth > 0)
            {
                if (Check("(")) depth++;
                if (Check(")")) { depth--; if (depth == 0) { Advance(); break; } }
                Advance();
            }
        }
        else
        {
            // single line
            while (!IsAtEnd() && CurrentLine() == tokens[_pos > 0 ? _pos - 1 : 0].Line)
                Advance();
        }
    }

    #region Token helpers

    private GoToken Current() => _pos < tokens.Count ? tokens[_pos] : tokens[^1];
    private GoToken Peek() => _pos + 1 < tokens.Count ? tokens[_pos + 1] : tokens[^1];
    private int CurrentLine() => Current().Line;
    private bool IsAtEnd() => _pos >= tokens.Count || Current().Kind == GoTokenKind.Eof;
    private void Advance() { if (!IsAtEnd()) _pos++; }

    private bool Check(string value) => !IsAtEnd() && Current().Value == value;
    private bool CheckKeyword(string kw) => !IsAtEnd() && Current().Kind == GoTokenKind.Keyword && Current().Value == kw;
    private bool MatchKeyword(string kw) { if (CheckKeyword(kw)) { Advance(); return true; } return false; }

    private string ConsumeIdentifier()
    {
        if (!IsAtEnd() && Current().Kind == GoTokenKind.Identifier)
        { var v = Current().Value; Advance(); return v; }
        return "";
    }

    private string ConsumeType(bool includeBraces = false)
    {
        int start = _pos;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("[") || Check("(")) { depth++; Advance(); continue; }
            if (Check("]") || Check(")")) { if (depth == 0) break; depth--; Advance(); continue; }
            if (includeBraces)
            {
                if (Check("{")) { depth++; Advance(); continue; }
                if (Check("}")) { if (depth == 0) break; depth--; Advance(); continue; }
            }
            else
            {
                if (Check("{") || Check("}")) break;
            }
            if (depth == 0 && (Check(",") || Check(";") || Check("\n"))) break;
            // A struct tag (raw/interpreted string) is not part of the field type — stop here
            // so the tag stays as the current token for tag detection and the next field parses.
            if (depth == 0 && Current().Kind == GoTokenKind.StringLiteral) break;
            // Stop at keywords that start new declarations
            if (depth == 0 && (CheckKeyword("func") || CheckKeyword("type") || CheckKeyword("var") || CheckKeyword("const"))) break;
            Advance();
        }
        if (_pos == start) return "";
        return string.Join("", tokens[start.._pos].Select(t => t.Value));
    }

    private void SkipGenerics()
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

    private void SkipComments()
    {
        while (!IsAtEnd() && Current().Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment)
            Advance();
    }

    private bool HasPrecedingDocComment()
    {
        for (int i = _pos - 1; i >= 0 && i >= _pos - 5; i--)
        {
            if (tokens[i].Kind == GoTokenKind.LineComment && tokens[i].Value.StartsWith("//"))
                return true;
            if (tokens[i].Kind == GoTokenKind.BlockComment) return true;
            continue;
        }
        return false;
    }

    private HashSet<int> ExtractCommentLines()
    {
        var lines = new HashSet<int>();
        foreach (var t in tokens)
            if (t.Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment)
                lines.Add(t.Line);
        return lines;
    }

    #endregion
}

#endregion
