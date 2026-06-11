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
        var parser = new GoParser(tokens, sourceText);
        return parser.Parse(filePath);
    }
}

#region Lexer

internal enum GoTokenKind
{
    Identifier, Keyword, Punctuation, StringLiteral, NumberLiteral,
    LineComment, BlockComment, Eof
}

internal record struct GoToken(GoTokenKind Kind, string Value, int Line, int Start, int End);

internal class GoLexer(string source)
{
    private int _pos;
    private int _line = 1;

    private static readonly HashSet<string> Keywords =
    [
        "break", "case", "chan", "const", "continue", "default", "defer",
        "else", "fallthrough", "for", "func", "go", "goto", "if", "import",
        "interface", "map", "package", "range", "return", "select", "struct",
        "switch", "type", "var"
    ];

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

            // Raw string (backtick)
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

        tokens.Add(new GoToken(GoTokenKind.Eof, "", _line, _pos, _pos));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < source.Length)
        {
            char c = source[_pos];
            if (c == '\n') { _line++; _pos++; }
            else if (c is ' ' or '\t' or '\r') _pos++;
            else break;
        }
    }

    private GoToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && source[_pos] != '\n') _pos++;
        return new GoToken(GoTokenKind.LineComment, source[start.._pos], line, start, _pos);
    }

    private GoToken ReadBlockComment()
    {
        int start = _pos;
        int line = _line;
        _pos += 2;
        while (_pos < source.Length)
        {
            if (source[_pos] == '*' && _pos + 1 < source.Length && source[_pos + 1] == '/')
            { _pos += 2; break; }
            if (source[_pos] == '\n') _line++;
            _pos++;
        }
        return new GoToken(GoTokenKind.BlockComment, source[start.._pos], line, start, _pos);
    }

    private GoToken ReadString()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip "
        while (_pos < source.Length && source[_pos] != '"')
        {
            if (source[_pos] == '\\') _pos++;
            _pos++;
        }
        if (_pos < source.Length) _pos++;
        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private GoToken ReadRawString()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip `
        while (_pos < source.Length && source[_pos] != '`')
        {
            if (source[_pos] == '\n') _line++;
            _pos++;
        }
        if (_pos < source.Length) _pos++;
        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private GoToken ReadRune()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        if (_pos < source.Length && source[_pos] == '\\') _pos++;
        if (_pos < source.Length) _pos++;
        if (_pos < source.Length && source[_pos] == '\'') _pos++;
        return new GoToken(GoTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private GoToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '.' or '_' or 'x' or 'X'))
            _pos++;
        return new GoToken(GoTokenKind.NumberLiteral, source[start.._pos], line, start, _pos);
    }

    private GoToken ReadIdentifierOrKeyword()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            _pos++;
        var value = source[start.._pos];
        var kind = Keywords.Contains(value) ? GoTokenKind.Keyword : GoTokenKind.Identifier;
        return new GoToken(kind, value, line, start, _pos);
    }

    private GoToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        if (_pos + 2 < source.Length)
        {
            var three = source.Substring(_pos, 3);
            if (three is "..." or "<<=" or ">>=")
            { _pos += 3; return new GoToken(GoTokenKind.Punctuation, three, line, start, _pos); }
        }
        if (_pos + 1 < source.Length)
        {
            var two = source.Substring(_pos, 2);
            if (two is ":=" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<-" or "++" or "--" or "+=" or "-=" or "*=" or "/=" or "<<" or ">>")
            { _pos += 2; return new GoToken(GoTokenKind.Punctuation, two, line, start, _pos); }
        }
        _pos++;
        return new GoToken(GoTokenKind.Punctuation, source[start.._pos], line, start, _pos);
    }
}

#endregion

#region Parser

internal class GoParser(List<GoToken> tokens, string sourceText)
{
    private int _pos;

    public SourceFile Parse(string filePath)
    {
        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
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
                // Top-level funcs: add them grouped if needed
                if (method != null && method.Value.receiver == null)
                {
                    // Free function — could attach to a synthetic type or ignore
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

        return new SourceFile(filePath, "go", types, statements, sourceText)
        {
            Namespace = ns,
            Usings = usings,
            Regions = [],
            CommentLines = ExtractCommentLines()
        };
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
        string name = ConsumeIdentifier();
        if (name == "") return null;
        int line = CurrentLine();

        bool isExported = char.IsUpper(name[0]);
        var modifiers = isExported ? Modifier.Public : Modifier.Private;

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
            string aliasType = ConsumeType();
            return new TypeDeclaration(name, TypeKind.Struct, modifiers, [], [], [], [], [], [], line)
            { HasDocComment = hasDoc };
        }
    }

    private TypeDeclaration ParseStructType(string name, Modifier modifiers, bool hasDoc, int line)
    {
        Advance(); // skip 'struct'
        var fields = new List<FieldDeclaration>();
        var embedded = new List<string>();

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
                        // field Name Type
                        fieldName = first;
                        string fieldType = ConsumeType();
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
                else Advance();

                // Skip struct tags
                if (Current().Kind == GoTokenKind.StringLiteral) Advance();
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Struct, modifiers, embedded, [], [], [], [], [], line)
        { HasDocComment = hasDoc, Fields = fields };
    }

    private TypeDeclaration ParseInterfaceType(string name, Modifier modifiers, bool hasDoc, int line, List<StatementInfo> statements)
    {
        Advance(); // skip 'interface'
        var methods = new List<MethodDeclaration>();
        var embedded = new List<string>();

        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipComments();
                if (Check("}")) break;

                if (Current().Kind == GoTokenKind.Identifier)
                {
                    string methodOrType = Current().Value;
                    int mLine = CurrentLine();
                    Advance();

                    if (Check("("))
                    {
                        // Method signature
                        var parameters = ParseParamList();
                        TypeReference? returnType = ParseReturnType();
                        bool exported = char.IsUpper(methodOrType[0]);
                        methods.Add(new MethodDeclaration(methodOrType,
                            exported ? Modifier.Public : Modifier.Private, [], returnType, parameters, mLine));
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
        { HasDocComment = hasDoc };
    }

    private (MethodDeclaration? method, string? receiver)? ParseFunc(List<StatementInfo> statements)
    {
        bool hasDoc = HasPrecedingDocComment();
        Advance(); // skip 'func'

        string? receiver = null;
        string? receiverType = null;

        // Method receiver: func (r *Type) Name(...)
        if (Check("("))
        {
            Advance();
            if (Current().Kind == GoTokenKind.Identifier)
            {
                receiver = Current().Value;
                Advance();
            }
            if (Check("*")) Advance();
            if (Current().Kind == GoTokenKind.Identifier)
            {
                receiverType = Current().Value;
                Advance();
            }
            SkipGenerics();
            if (Check(")")) Advance();
        }

        string name = ConsumeIdentifier();
        if (name == "") { SkipBraces(); return null; }
        int line = CurrentLine();
        SkipGenerics();

        var parameters = ParseParamList();
        TypeReference? returnType = ParseReturnType();

        bool isExported = char.IsUpper(name[0]);
        var modifiers = isExported ? Modifier.Public : Modifier.Private;

        var methodStatements = new List<StatementInfo>();
        if (Check("{"))
        {
            ParseBlock(methodStatements);
        }
        statements.AddRange(methodStatements);

        var method = new MethodDeclaration(name, modifiers, [], returnType, parameters, line)
        { Statements = methodStatements, HasDocComment = hasDoc };

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
                string varType = ConsumeType();
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
                    if (Check("...")) Advance();
                    string paramType = ConsumeType();
                    foreach (var n in names)
                        parameters.Add(new ParameterDeclaration(n, new TypeReference(paramType, null, [], paramType), false, false, false, 0));
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
                // type without name (e.g. *Type, []byte, etc.)
                string paramType = ConsumeType();
                foreach (var n in names)
                    parameters.Add(new ParameterDeclaration(n, new TypeReference(paramType, null, [], paramType), false, false, false, 0));
                names.Clear();
                if (names.Count == 0 && paramType != "")
                    parameters.Add(new ParameterDeclaration("", new TypeReference(paramType, null, [], paramType), false, false, false, 0));
                if (Check(",")) Advance();
            }
        }
        // If we have leftover names, they're types
        foreach (var n in names)
            parameters.Add(new ParameterDeclaration("", new TypeReference(n, null, [], n), false, false, false, 0));
        if (Check(")")) Advance();
        return parameters;
    }

    private TypeReference? ParseReturnType()
    {
        if (Check("("))
        {
            // Multiple returns
            int start = _pos;
            SkipParens();
            string multi = string.Join("", tokens[start.._pos].Select(t => t.Value));
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

    private void ParseBlock(List<StatementInfo> statements)
    {
        if (!Check("{")) return;
        Advance();
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            if (Check("{")) { depth++; Advance(); continue; }
            if (Check("}")) { depth--; if (depth == 0) { Advance(); break; } Advance(); continue; }

            // Detect panic/recover
            if (Current().Kind == GoTokenKind.Identifier && Current().Value is "panic")
            {
                int stmtLine = CurrentLine();
                Advance();
                if (Check("(")) SkipParens();
                statements.Add(new StatementInfo("throw", [], null, "panic", [], stmtLine, true));
                continue;
            }
            if (Current().Kind == GoTokenKind.Identifier && Current().Value is "recover")
            {
                int stmtLine = CurrentLine();
                Advance();
                if (Check("(")) SkipParens();
                statements.Add(new StatementInfo("catch", [], null, "recover", [], stmtLine, true));
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
                    statements.Add(new StatementInfo("call", [],
                        typeName != "" ? typeName : null, memberName, [], stmtLine, true));
                    SkipParens();
                }
                continue;
            }

            Advance();
        }
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

    private string ConsumeType()
    {
        int start = _pos;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("[") || Check("(")) { depth++; Advance(); continue; }
            if (Check("]") || Check(")")) { if (depth == 0) break; depth--; Advance(); continue; }
            if (Check("{") || Check("}")) break;
            if (depth == 0 && (Check(",") || Check(";") || Check("\n"))) break;
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
