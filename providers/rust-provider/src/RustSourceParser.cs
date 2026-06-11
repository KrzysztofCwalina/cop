using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class RustSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".rs"];
    public override string Language => "rust";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lexer = new RustLexer(sourceText);
        var tokens = lexer.Tokenize();
        var parser = new RustParser(tokens, sourceText);
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

            // String literals
            if (c == '"')
            {
                tokens.Add(ReadStringLiteral());
                continue;
            }

            // Raw strings: r"..." r#"..."#
            if (c == 'r' && _pos + 1 < source.Length && (source[_pos + 1] == '"' || source[_pos + 1] == '#'))
            {
                tokens.Add(ReadRawString());
                continue;
            }

            // Char literal
            if (c == '\'' && _pos + 1 < source.Length && source[_pos + 1] != '\'')
            {
                // Could be a lifetime or a char literal
                if (_pos + 2 < source.Length && char.IsLetter(source[_pos + 1]) && !IsAfterIdentifier(tokens))
                {
                    tokens.Add(ReadLifetime());
                    continue;
                }
                tokens.Add(ReadCharLiteral());
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
        while (_pos < source.Length && source[_pos] is ' ' or '\t' or '\r')
            _pos++;
        if (_pos < source.Length && source[_pos] == '\n')
        {
            _line++;
            _pos++;
            SkipWhitespace();
        }
    }

    private RustToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        _pos += 2; // skip //
        bool isDoc = _pos < source.Length && source[_pos] == '/';
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
        return new RustToken(RustTokenKind.BlockComment, source[start.._pos], line, start, _pos);
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
                if (source[_pos] == '[') depth++;
                else if (source[_pos] == ']') { depth--; if (depth == 0) { _pos++; break; } }
                else if (source[_pos] == '\n') _line++;
                _pos++;
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
            if (source[_pos] == '\\') _pos++; // skip escape
            if (_pos < source.Length && source[_pos] == '\n') _line++;
            _pos++;
        }
        if (_pos < source.Length) _pos++; // skip closing "
        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadRawString()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip r
        int hashes = 0;
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
                if (endHashes == hashes) break;
            }
            else
            {
                if (source[_pos] == '\n') _line++;
                _pos++;
            }
        }
        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadCharLiteral()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        if (_pos < source.Length && source[_pos] == '\\') _pos++;
        if (_pos < source.Length) _pos++;
        if (_pos < source.Length && source[_pos] == '\'') _pos++;
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

    private RustToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '.' or '_'))
            _pos++;
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

    private RustToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        // Multi-char operators
        if (_pos + 1 < source.Length)
        {
            var two = source.Substring(_pos, 2);
            if (two is "::" or "->" or "=>" or "&&" or "||" or "==" or "!=" or ">=" or "<=" or ".." or "+=" or "-=" or "*=" or "/=")
            {
                _pos += 2;
                return new RustToken(RustTokenKind.Punctuation, two, line, start, _pos);
            }
        }
        _pos++;
        return new RustToken(RustTokenKind.Punctuation, source[start.._pos], line, start, _pos);
    }

    private static bool IsAfterIdentifier(List<RustToken> tokens) =>
        tokens.Count > 0 && tokens[^1].Kind is RustTokenKind.Identifier or RustTokenKind.Keyword;
}

#endregion

#region Parser

internal class RustParser(List<RustToken> tokens, string sourceText)
{
    private int _pos;

    public SourceFile Parse(string filePath)
    {
        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var usings = new List<string>();

        while (!IsAtEnd())
        {
            SkipCommentsAndAttributes();
            if (IsAtEnd()) break;

            if (MatchKeyword("use"))
            {
                ParseUse(usings);
            }
            else if (CheckKeyword("pub") || CheckKeyword("struct") || CheckKeyword("enum") || CheckKeyword("trait") || CheckKeyword("impl") || CheckKeyword("unsafe"))
            {
                var type = TryParseTypeOrImpl(statements);
                if (type != null) types.Add(type);
                else Advance();
            }
            else if (CheckKeyword("fn") || (CheckKeyword("pub") || CheckKeyword("async")))
            {
                if (IsFnAhead())
                {
                    var (method, _) = ParseFn(statements);
                    Advance(); // handled
                }
                else Advance();
            }
            else
            {
                Advance();
            }
        }

        return new SourceFile(filePath, "rust", types, statements, sourceText)
        {
            Usings = usings,
            Regions = [],
            CommentLines = ExtractCommentLines()
        };
    }

    private TypeDeclaration? TryParseTypeOrImpl(List<StatementInfo> statements)
    {
        int savedPos = _pos;
        var attributes = CollectPrecedingAttributes();
        bool hasDoc = HasPrecedingDocComment();

        // Handle visibility
        bool isPub = MatchKeyword("pub");
        if (isPub && Check("(")) SkipParens(); // pub(crate) etc.

        if (MatchKeyword("unsafe")) { /* unsafe trait/impl */ }

        if (CheckKeyword("struct")) return ParseStruct(isPub, attributes, hasDoc);
        if (CheckKeyword("enum")) return ParseEnum(isPub, attributes, hasDoc);
        if (CheckKeyword("trait")) return ParseTrait(isPub, attributes, hasDoc, statements);
        if (CheckKeyword("impl")) return ParseImpl(statements);
        if (CheckKeyword("fn") || CheckKeyword("async"))
        {
            var (method, _) = ParseFn(statements);
            _pos = savedPos;
            return null;
        }

        _pos = savedPos;
        return null;
    }

    private TypeDeclaration ParseStruct(bool isPub, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance(); // skip 'struct'
        string name = ConsumeIdentifier();
        SkipGenerics();

        var modifiers = isPub ? Modifier.Public : Modifier.Private;
        var fields = new List<FieldDeclaration>();

        if (Check(";")) { Advance(); } // unit struct
        else if (Check("(")) { SkipParens(); if (Check(";")) Advance(); } // tuple struct
        else if (Check("{") || MatchKeyword("where"))
        {
            if (CheckKeyword("where")) SkipUntil("{");
            if (Check("{"))
            {
                fields = ParseStructFields();
            }
        }

        return new TypeDeclaration(name, TypeKind.Struct, modifiers, [], attributes, [], [], [], [], line)
        { HasDocComment = hasDoc, Fields = fields };
    }

    private List<FieldDeclaration> ParseStructFields()
    {
        var fields = new List<FieldDeclaration>();
        Advance(); // skip {
        while (!IsAtEnd() && !Check("}"))
        {
            SkipCommentsAndAttributes();
            if (Check("}")) break;

            bool fieldPub = MatchKeyword("pub");
            if (fieldPub && Check("(")) SkipParens();

            if (Check("}")) break;
            string fieldName = ConsumeIdentifier();
            if (fieldName == "" || !Check(":")) { SkipUntilCommaOrBrace(); continue; }
            Advance(); // skip :
            string fieldType = ConsumeType();
            var vis = fieldPub ? Modifier.Public : Modifier.Private;
            fields.Add(new FieldDeclaration(fieldName, new TypeReference(fieldType, null, [], fieldType), vis, CurrentLine()));
            if (Check(",")) Advance();
        }
        if (Check("}")) Advance();
        return fields;
    }

    private TypeDeclaration ParseEnum(bool isPub, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance(); // skip 'enum'
        string name = ConsumeIdentifier();
        SkipGenerics();
        if (CheckKeyword("where")) SkipUntil("{");

        var modifiers = isPub ? Modifier.Public : Modifier.Private;
        var variants = new List<string>();

        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                string variantName = ConsumeIdentifier();
                if (variantName != "") variants.Add(variantName);
                // Skip variant data
                if (Check("(")) SkipParens();
                if (Check("{")) SkipBraces();
                if (Check("=")) { Advance(); while (!IsAtEnd() && !Check(",") && !Check("}")) Advance(); }
                if (Check(",")) Advance();
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Enum, modifiers, [], attributes, [], [], [], variants, line)
        { HasDocComment = hasDoc };
    }

    private TypeDeclaration ParseTrait(bool isPub, List<string> attributes, bool hasDoc, List<StatementInfo> statements)
    {
        int line = CurrentLine();
        Advance(); // skip 'trait'
        string name = ConsumeIdentifier();
        SkipGenerics();

        var baseTypes = new List<string>();
        if (Check(":"))
        {
            Advance();
            while (!IsAtEnd() && !Check("{") && !CheckKeyword("where"))
            {
                SkipCommentsAndAttributes();
                string bt = ConsumeIdentifier();
                if (bt != "") baseTypes.Add(bt);
                SkipGenerics();
                if (Check("+")) Advance();
                else break;
            }
        }
        if (CheckKeyword("where")) SkipUntil("{");

        var methods = new List<MethodDeclaration>();
        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("unsafe"))
                {
                    var (m, _) = ParseFn(statements);
                    if (m != null) methods.Add(m);
                }
                else Advance();
            }
            if (Check("}")) Advance();
        }

        var modifiers = isPub ? Modifier.Public : Modifier.Private;
        return new TypeDeclaration(name, TypeKind.Interface, modifiers, baseTypes, attributes, [], methods, [], [], line)
        { HasDocComment = hasDoc };
    }

    private TypeDeclaration? ParseImpl(List<StatementInfo> statements)
    {
        int line = CurrentLine();
        Advance(); // skip 'impl'
        SkipGenerics();

        // Gather the type name(s)
        string firstName = ConsumeIdentifier();
        SkipGenerics();

        string typeName;
        var baseTypes = new List<string>();

        if (MatchKeyword("for"))
        {
            // impl Trait for Type
            typeName = ConsumeIdentifier();
            SkipGenerics();
            baseTypes.Add(firstName);
        }
        else
        {
            typeName = firstName;
        }

        if (CheckKeyword("where")) SkipUntil("{");

        var methods = new List<MethodDeclaration>();
        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                bool methodPub = MatchKeyword("pub");
                if (methodPub && Check("(")) SkipParens();
                if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("unsafe"))
                {
                    var (m, _) = ParseFn(statements);
                    if (m != null)
                    {
                        if (methodPub) m = m with { Modifiers = m.Modifiers | Modifier.Public };
                        methods.Add(m);
                    }
                }
                else Advance();
            }
            if (Check("}")) Advance();
        }

        var constructors = methods.Where(m => m.Name == "new").ToList();
        var regularMethods = methods.Where(m => m.Name != "new").ToList();

        string displayName = baseTypes.Count > 0 ? $"{typeName} (impl {baseTypes[0]})" : $"{typeName} (impl)";
        return new TypeDeclaration(displayName, TypeKind.Class, Modifier.Public, baseTypes, [], constructors, regularMethods, [], [], line);
    }

    private (MethodDeclaration?, string?) ParseFn(List<StatementInfo> statements)
    {
        bool hasDoc = HasPrecedingDocComment();
        var modifiers = Modifier.Private;

        if (MatchKeyword("async")) modifiers |= Modifier.Async;
        if (MatchKeyword("unsafe")) { }
        if (MatchKeyword("extern")) { if (Check("\"")) Advance(); } // extern "C"

        if (!MatchKeyword("fn")) return (null, null);

        int line = PreviousLine();
        string name = ConsumeIdentifier();
        SkipGenerics();

        // Parameters
        var parameters = new List<ParameterDeclaration>();
        if (Check("("))
        {
            Advance();
            while (!IsAtEnd() && !Check(")"))
            {
                SkipCommentsAndAttributes();
                if (Check(")")) break;
                // self params
                if (CheckKeyword("self") || Check("&"))
                {
                    SkipUntilCommaOrParen();
                    if (Check(",")) Advance();
                    continue;
                }
                if (MatchKeyword("mut")) { }
                string paramName = ConsumeIdentifier();
                if (Check(":"))
                {
                    Advance();
                    string paramType = ConsumeType();
                    parameters.Add(new ParameterDeclaration(paramName,
                        new TypeReference(paramType, null, [], paramType), false, false, false, 0));
                }
                if (Check(",")) Advance();
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
                returnType = new TypeReference(retType, null, [], retType);
        }

        if (CheckKeyword("where")) SkipUntil("{");

        // Body
        var methodStatements = new List<StatementInfo>();
        if (Check("{"))
        {
            ParseBlock(methodStatements);
        }
        else if (Check(";")) Advance();

        statements.AddRange(methodStatements);
        return (new MethodDeclaration(name, modifiers, [], returnType, parameters, line)
        { Statements = methodStatements, HasDocComment = hasDoc }, null);
    }

    private void ParseBlock(List<StatementInfo> statements)
    {
        if (!Check("{")) return;
        Advance(); // skip {
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            if (Check("{")) { depth++; Advance(); continue; }
            if (Check("}")) { depth--; if (depth == 0) { Advance(); break; } Advance(); continue; }

            // Detect panic!/todo!/unimplemented!
            if (Current().Kind == RustTokenKind.Identifier &&
                Current().Value is "panic" or "todo" or "unimplemented" or "unreachable" &&
                Peek().Value == "!")
            {
                string macroName = Current().Value;
                int stmtLine = CurrentLine();
                statements.Add(new StatementInfo("throw", [], null, macroName, [], stmtLine, true));
                Advance(); Advance(); // skip name and !
                if (Check("(")) SkipParens();
                continue;
            }

            // Detect function/method calls: ident( or ident::ident( or expr.ident(
            if (Current().Kind == RustTokenKind.Identifier)
            {
                int stmtLine = CurrentLine();
                string typeName = "";
                string memberName = Current().Value;
                Advance();

                // path::ident
                while (Check("::"))
                {
                    Advance();
                    if (Current().Kind == RustTokenKind.Identifier)
                    {
                        typeName = typeName == "" ? memberName : typeName + "::" + memberName;
                        memberName = Current().Value;
                        Advance();
                    }
                }

                // .method calls
                if (Check(".") && Peek().Kind == RustTokenKind.Identifier)
                {
                    typeName = memberName;
                    Advance(); // skip .
                    memberName = Current().Value;
                    Advance();
                }

                if (Check("(") && memberName is not "if" and not "for" and not "while" and not "loop" and not "match")
                {
                    statements.Add(new StatementInfo("call", [],
                        typeName != "" ? typeName : null, memberName, [], stmtLine, true));
                    SkipParens();
                }
                // Macro calls: ident!(...)
                else if (Check("!"))
                {
                    Advance();
                    string macroCall = memberName + "!";
                    if (Check("(")) SkipParens();
                    else if (Check("[")) SkipBrackets();
                    else if (Check("{")) SkipBraces();
                    statements.Add(new StatementInfo("call", [], null, macroCall, [], stmtLine, true));
                }
                continue;
            }

            Advance();
        }
    }

    // Helper: parse a use statement
    private void ParseUse(List<string> usings)
    {
        var parts = new List<string>();
        while (!IsAtEnd() && !Check(";"))
        {
            if (Check("{"))
            {
                string basePath = string.Join("::", parts);
                Advance();
                while (!IsAtEnd() && !Check("}"))
                {
                    if (Current().Kind == RustTokenKind.Identifier)
                    {
                        usings.Add(basePath == "" ? Current().Value : basePath + "::" + Current().Value);
                    }
                    Advance();
                }
                if (Check("}")) Advance();
                parts.Clear();
                break;
            }
            if (Current().Kind == RustTokenKind.Identifier || Current().Kind == RustTokenKind.Keyword)
                parts.Add(Current().Value);
            Advance();
        }
        if (parts.Count > 0) usings.Add(string.Join("::", parts));
        if (Check(";")) Advance();
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
    private bool MatchKeyword(string kw) { if (CheckKeyword(kw)) { Advance(); return true; } return false; }

    private string ConsumeIdentifier()
    {
        if (!IsAtEnd() && Current().Kind == RustTokenKind.Identifier)
        { var v = Current().Value; Advance(); return v; }
        if (!IsAtEnd() && Current().Kind == RustTokenKind.Keyword && Current().Value == "Self")
        { Advance(); return "Self"; }
        return "";
    }

    private string ConsumeType()
    {
        int start = _pos;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("<")) { depth++; Advance(); continue; }
            if (Check(">")) { if (depth == 0) break; depth--; Advance(); continue; }
            if (depth == 0 && (Check(",") || Check(";") || Check("{") || Check("}") || Check(")") || CheckKeyword("where"))) break;
            Advance();
        }
        if (_pos == start) return "";
        return string.Join("", tokens[start.._pos].Select(t => t.Value));
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
                lines.Add(t.Line);
        return lines;
    }

    #endregion
}

#endregion
