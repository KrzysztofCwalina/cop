using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class JavaSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".java"];
    public override string Language => "java";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lexer = new JavaLexer(sourceText);
        var tokens = lexer.Tokenize();
        var parser = new JavaParser(tokens, lexer.Errors, sourceText);
        return parser.Parse(filePath);
    }
}

#region Lexer

internal enum JavaTokenKind
{
    Identifier, Keyword, Punctuation, StringLiteral, NumberLiteral,
    LineComment, BlockComment, DocComment, Annotation, Eof
}

internal record struct JavaToken(JavaTokenKind Kind, string Value, int Line, int Start, int End);

internal class JavaLexer(string source)
{
    private int _pos;
    private int _line = 1;
    private readonly List<(int line, int col, string msg)> _errors = [];
    public IReadOnlyList<(int line, int col, string msg)> Errors => _errors;

    private static readonly HashSet<string> Keywords =
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

    public List<JavaToken> Tokenize()
    {
        var tokens = new List<JavaToken>();
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
                tokens.Add(ReadChar());
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

        tokens.Add(new JavaToken(JavaTokenKind.Eof, "", _line, _pos, _pos));
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

    private JavaToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && source[_pos] != '\n') _pos++;
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
            if (source[_pos] == '\n') _line++;
            _pos++;
        }
        if (!closed)
            _errors.Add((line, 1, "Unterminated block comment"));
        return new JavaToken(isDoc ? JavaTokenKind.DocComment : JavaTokenKind.BlockComment, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadAnnotation()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip @
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '_' or '.'))
            _pos++;
        // Include parenthesized arguments
        if (_pos < source.Length && source[_pos] == '(')
        {
            int depth = 0;
            while (_pos < source.Length)
            {
                if (source[_pos] == '(') depth++;
                else if (source[_pos] == ')') { depth--; if (depth == 0) { _pos++; break; } }
                else if (source[_pos] == '\n') _line++;
                _pos++;
            }
        }
        return new JavaToken(JavaTokenKind.Annotation, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadString()
    {
        int start = _pos;
        int line = _line;
        // Text block """..."""
        if (_pos + 2 < source.Length && source[_pos + 1] == '"' && source[_pos + 2] == '"')
        {
            _pos += 3;
            bool closed = false;
            while (_pos + 2 < source.Length)
            {
                if (source[_pos] == '"' && source[_pos + 1] == '"' && source[_pos + 2] == '"')
                { _pos += 3; closed = true; break; }
                if (source[_pos] == '\n') _line++;
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
            if (ch == '\\') { _pos++; if (_pos < source.Length) _pos++; continue; }
            if (ch == '"') { _pos++; stringClosed = true; break; }
            if (ch == '\n') break; // unterminated on newline
            _pos++;
        }
        if (!stringClosed)
            _errors.Add((line, 1, "Unterminated string literal"));
        return new JavaToken(JavaTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadChar()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        if (_pos < source.Length && source[_pos] == '\\') _pos++;
        if (_pos < source.Length) _pos++;
        if (_pos < source.Length && source[_pos] == '\'') _pos++;
        return new JavaToken(JavaTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '.' or '_'))
            _pos++;
        return new JavaToken(JavaTokenKind.NumberLiteral, source[start.._pos], line, start, _pos);
    }

    private JavaToken ReadIdentifierOrKeyword()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '_' or '$'))
            _pos++;
        if (source[start.._pos] == "non"
            && _pos + 7 <= source.Length
            && source[_pos] == '-'
            && source.AsSpan(_pos + 1, 6).SequenceEqual("sealed".AsSpan())
            && (_pos + 7 == source.Length || !(char.IsLetterOrDigit(source[_pos + 7]) || source[_pos + 7] is '_' or '$')))
        {
            _pos += 7;
        }
        var value = source[start.._pos];
        var kind = Keywords.Contains(value) ? JavaTokenKind.Keyword : JavaTokenKind.Identifier;
        return new JavaToken(kind, value, line, start, _pos);
    }

    private JavaToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        if (_pos + 2 < source.Length)
        {
            var three = source.Substring(_pos, 3);
            if (three is ">>>" or "...") { _pos += 3; return new JavaToken(JavaTokenKind.Punctuation, three, line, start, _pos); }
        }
        if (_pos + 1 < source.Length)
        {
            var two = source.Substring(_pos, 2);
            if (two is "::" or "->" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "++" or "--" or "+=" or "-=" or "*=" or "/=" or "<<" or ">>" or "|=" or "&=" or "^=")
            { _pos += 2; return new JavaToken(JavaTokenKind.Punctuation, two, line, start, _pos); }
        }
        _pos++;
        return new JavaToken(JavaTokenKind.Punctuation, source[start.._pos], line, start, _pos);
    }
}

#endregion

#region Parser

internal class JavaParser(List<JavaToken> tokens, IReadOnlyList<(int line, int col, string msg)> lexerErrors, string sourceText)
{
    private int _pos;
    private string _filePath = "";
    private readonly List<string> _parseErrors = [];
    private static bool _diag => Cop.Core.CopDiagnostics.Timing;

    /// <summary>
    /// Progress guard for parser loops: if a loop iteration consumed no tokens, force a
    /// single advance so the parser can never hang on an unexpected token sequence.
    /// Returns the (possibly advanced) position to use as the next iteration's baseline.
    /// </summary>
    private int Guard(int before, string where)
    {
        if (_pos == before && !IsAtEnd())
        {
            if (_diag)
                Console.Error.WriteLine($"[java-parser] non-advancing loop in {where} at token #{_pos} '{Current().Value}' ({Current().Kind}); forcing advance");
            Advance();
        }
        return _pos;
    }

    public SourceFile Parse(string filePath)
    {
        _filePath = filePath;
        foreach (var (line, col, msg) in lexerErrors)
            _parseErrors.Add($"{filePath}({line},{col}): error: {msg}");

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
                ns = ConsumeQualifiedName();
                if (Check(";")) Advance();
            }
            else if (MatchKeyword("import"))
            {
                if (MatchKeyword("static")) { }
                usings.Add(ConsumeQualifiedName());
                if (Check(";")) Advance();
            }
            else if (IsTypeStart())
            {
                var type = ParseType(statements);
                if (type != null) types.Add(type);
            }
            else
            {
                Advance();
            }
        }

        // Only check brace balance when the lexer found no unterminated constructs
        // (lexer errors may truncate the token stream and cause spurious brace mismatches).
        if (lexerErrors.Count == 0)
            CheckBraceBalance();

        return new SourceFile(filePath, "java", types, statements, sourceText)
        {
            Namespace = ns,
            Usings = usings,
            Regions = [],
            CommentLines = ExtractCommentLines(),
            ParseErrors = [.. _parseErrors]
        };
    }

    private bool IsTypeStart()
    {
        // Check if current position starts a type declaration (with annotations/modifiers)
        int saved = _pos;
        SkipAnnotations();
        // Skip modifier keywords
        while (!IsAtEnd() && Current().Kind == JavaTokenKind.Keyword &&
               Current().Value is "public" or "private" or "protected" or "static" or "abstract" or "final" or "sealed" or "strictfp" or "synchronized" or "native" or "volatile" or "transient" or "default" or "non-sealed")
            Advance();
        bool isType = CheckKeyword("class") || CheckKeyword("interface") || CheckKeyword("enum") || CheckKeyword("record");
        _pos = saved;
        return isType;
    }

    private TypeDeclaration? ParseType(List<StatementInfo> allStatements)
    {
        var annotations = CollectAnnotations();
        bool hasDoc = HasPrecedingDocComment();
        var modifiers = ParseModifiers(out var isSealed, out var isNonSealed, out var isFinal);
        SkipComments();

        TypeKind kind;
        if (MatchKeyword("class")) kind = TypeKind.Class;
        else if (MatchKeyword("interface")) kind = TypeKind.Interface;
        else if (MatchKeyword("enum")) kind = TypeKind.Enum;
        else if (MatchKeyword("record")) kind = TypeKind.Struct; // records as struct
        else return null;

        string name = ConsumeIdentifier();
        if (name == "")
        {
            AddError(CurrentLine(), 1, "Expected type name");
            return null;
        }
        int line = CurrentLine();
        bool isGeneric = Check("<");
        SkipGenerics(); // type parameters

        // Record components
        var fields = new List<FieldDeclaration>();
        if (kind == TypeKind.Struct && Check("("))
        {
            fields = ParseRecordComponents();
        }

        // extends/implements
        var baseTypes = new List<string>();
        if (MatchKeyword("extends"))
        {
            baseTypes.Add(ConsumeQualifiedName());
            SkipGenerics();
            while (Check(",")) { Advance(); baseTypes.Add(ConsumeQualifiedName()); SkipGenerics(); }
        }
        if (MatchKeyword("implements") || MatchKeyword("permits"))
        {
            baseTypes.Add(ConsumeQualifiedName());
            SkipGenerics();
            while (Check(",")) { Advance(); baseTypes.Add(ConsumeQualifiedName()); SkipGenerics(); }
        }

        // Body
        var methods = new List<MethodDeclaration>();
        var constructors = new List<MethodDeclaration>();
        var nestedTypes = new List<TypeDeclaration>();
        var enumValues = new List<string>();

        if (Check("{"))
        {
            Advance();
            // Enum values first
            if (kind == TypeKind.Enum)
            {
                ParseEnumConstants(enumValues);
            }

            while (!IsAtEnd() && !Check("}"))
            {
                int g = _pos;
                SkipComments();
                if (Check("}")) break;
                SkipAnnotations();

                if (IsTypeStart())
                {
                    var nested = ParseType(allStatements);
                    if (nested != null) nestedTypes.Add(nested);
                }
                else
                {
                    var member = ParseMember(name, allStatements, fields);
                    if (member != null)
                    {
                        if (member.Name == name || member.Name == "<init>")
                            constructors.Add(member);
                        else
                            methods.Add(member);
                    }
                }
                Guard(g, "class-body");
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, kind, modifiers, baseTypes, annotations, constructors, methods, nestedTypes, enumValues, line)
        { HasDocComment = hasDoc, Fields = fields }
            .AsJava(
                isRecord: kind == TypeKind.Struct,
                isEnum: kind == TypeKind.Enum,
                isSealed: isSealed,
                isNonSealed: isNonSealed,
                isFinal: isFinal,
                isGeneric: isGeneric);
    }

    private void ParseEnumConstants(List<string> enumValues)
    {
        while (!IsAtEnd() && !Check("}") && !Check(";"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check("}") || Check(";")) break;
            if (Current().Kind == JavaTokenKind.Identifier)
            {
                enumValues.Add(Current().Value);
                Advance();
                if (Check("(")) SkipParens();
                if (Check("{")) SkipBraces();
            }
            if (Check(",")) Advance();
            Guard(g, "enum-constants");
        }
        if (Check(";")) Advance();
    }

    private List<FieldDeclaration> ParseRecordComponents()
    {
        var fields = new List<FieldDeclaration>();
        Advance(); // skip (
        while (!IsAtEnd() && !Check(")"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check(")")) break;
            string paramType = ConsumeTypeName();
            string paramName = ConsumeIdentifier();
            if (paramName != "")
                fields.Add(new FieldDeclaration(paramName, new TypeReference(paramType, null, [], paramType), Modifier.Public, CurrentLine()));
            if (Check(",")) Advance();
            Guard(g, "record-components");
        }
        if (Check(")")) Advance();
        return fields;
    }

    private MethodDeclaration? ParseMember(string typeName, List<StatementInfo> allStatements, List<FieldDeclaration> fields)
    {
        SkipComments();
        bool hasDoc = HasPrecedingDocComment();
        var annotations = CollectAnnotations();
        var modifiers = ParseModifiers(
            out _, out _, out _,
            out var isSynchronized,
            out var isNative,
            out var isDefault,
            out var isStrictfp);

        // Skip type parameters on methods
        bool isGeneric = Check("<");
        SkipGenerics();
        SkipComments();

        if (IsAtEnd() || Check("}")) return null;

        // Static/instance initializer block
        if (Check("{"))
        {
            var initStatements = new List<StatementInfo>();
            ParseBlock(initStatements);
            allStatements.AddRange(initStatements);
            return null;
        }

        // Determine if this is a constructor (name matches type name and no return type)
        // or a method/field
        string firstType = ConsumeTypeName();
        if (firstType == "") { Advance(); return null; }

        // Constructor: TypeName(...)
        if (firstType == typeName && Check("("))
        {
            var parameters = ParseParameters();
            SkipThrows();
            var body = new List<StatementInfo>();
            if (Check("{")) ParseBlock(body);
            allStatements.AddRange(body);
            return new JavaMethodDeclaration(
                "<init>",
                modifiers,
                annotations,
                null,
                parameters,
                CurrentLine(),
                isSynchronized,
                isNative,
                isDefault,
                isStrictfp,
                isGeneric)
            { Statements = body, HasDocComment = hasDoc };
        }

        // Could be a field or method
        SkipGenerics();
        if (Check("[")) { Advance(); if (Check("]")) Advance(); } // array type

        if (Current().Kind != JavaTokenKind.Identifier
            && !(Current().Kind == JavaTokenKind.Keyword && ContextualKeywords.Contains(Current().Value)))
        {
            // Not a valid member — skip to next semicolon or brace
            SkipUntilSemiOrBrace();
            return null;
        }

        string memberName = ConsumeIdentifier();

        // Method
        if (Check("("))
        {
            var parameters = ParseParameters();
            SkipThrows();
            TypeReference? returnType = new TypeReference(firstType, null, [], firstType);
            var body = new List<StatementInfo>();
            if (Check("{")) ParseBlock(body);
            else if (Check(";")) Advance();
            allStatements.AddRange(body);
            return new JavaMethodDeclaration(
                memberName,
                modifiers,
                annotations,
                returnType,
                parameters,
                CurrentLine(),
                isSynchronized,
                isNative,
                isDefault,
                isStrictfp,
                isGeneric)
            { Statements = body, HasDocComment = hasDoc };
        }

        // Field
        int fieldLine = CurrentLine();
        fields.Add(new FieldDeclaration(memberName, new TypeReference(firstType, null, [], firstType), modifiers, fieldLine));
        // Skip to ;
        while (!IsAtEnd() && !Check(";") && !Check("}"))
        {
            if (Check("{")) SkipBraces(); // array initializer
            else Advance();
        }
        if (Check(";")) Advance();
        return null;
    }

    private List<ParameterDeclaration> ParseParameters()
    {
        var parameters = new List<ParameterDeclaration>();
        if (!Check("(")) return parameters;
        Advance();
        while (!IsAtEnd() && !Check(")"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check(")")) break;
            if (MatchKeyword("final")) { }
            string paramType = ConsumeTypeName();
            SkipGenerics();
            if (Check("[")) { Advance(); if (Check("]")) Advance(); }
            bool isVariadic = false;
            if (Check("...")) { Advance(); isVariadic = true; }
            string paramName = ConsumeIdentifier();
            if (paramName != "")
                parameters.Add(new ParameterDeclaration(paramName, new TypeReference(paramType, null, [], paramType), isVariadic, false, false, 0));
            if (Check(",")) Advance();
            Guard(g, "parameters");
        }
        if (Check(")")) Advance();
        return parameters;
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

            // throw statement
            if (CheckKeyword("throw"))
            {
                int stmtLine = CurrentLine();
                Advance();
                string? excType = null;
                if (CheckKeyword("new"))
                {
                    Advance(); // skip "new"
                    excType = ConsumeQualifiedName();
                    SkipGenerics();
                }
                else if (Current().Kind == JavaTokenKind.Identifier)
                {
                    // throw variable — can't determine type statically; just advance past name
                    Advance();
                }
                statements.Add(new JavaStatementInfo("throw", [], excType, null, [], stmtLine, true));
                continue;
            }

            // assert statement
            if (CheckKeyword("assert"))
            {
                int stmtLine = CurrentLine();
                Advance();
                statements.Add(new JavaStatementInfo("assert", [], null, "assert", [], stmtLine, true));
                continue;
            }

            // catch clause
            if (CheckKeyword("catch"))
            {
                int stmtLine = CurrentLine();
                Advance();
                var (exceptionType, catchVar, isGeneric) = ParseCatchClause();
                bool hasRethrow = LookaheadForRethrow(catchVar);
                statements.Add(new JavaStatementInfo("catch", [], exceptionType, null, [], stmtLine, true)
                {
                    IsErrorHandler = true,
                    IsGenericErrorHandler = isGeneric,
                    HasRethrow = hasRethrow
                });
                continue;
            }

            // synchronized block
            if (CheckKeyword("synchronized"))
            {
                int stmtLine = CurrentLine();
                Advance();
                if (Check("(")) SkipParens();
                statements.Add(new JavaStatementInfo("synchronized", [], null, "synchronized", [], stmtLine, true));
                continue;
            }

            // try / try-with-resources
            if (CheckKeyword("try"))
            {
                int stmtLine = CurrentLine();
                Advance();
                bool isTryWithResources = Check("(");
                if (isTryWithResources) SkipParens();
                statements.Add(new JavaStatementInfo("try", [], null, "try", [], stmtLine, true)
                {
                    IsTryWithResources = isTryWithResources
                });
                continue;
            }

            // for / enhanced for
            if (CheckKeyword("for"))
            {
                int stmtLine = CurrentLine();
                Advance();
                bool isEnhancedFor = Check("(") && SkipParensContainsTopLevelColon();
                statements.Add(new JavaStatementInfo("for", [], null, "for", [], stmtLine, true)
                {
                    IsEnhancedFor = isEnhancedFor
                });
                continue;
            }

            // Method/constructor calls
            if (Current().Kind == JavaTokenKind.Identifier || CheckKeyword("this") || CheckKeyword("super"))
            {
                int stmtLine = CurrentLine();
                string memberName = Current().Value;
                Advance();

                // Chained: obj.method or Class.method
                while (Check(".") && Peek().Kind == JavaTokenKind.Identifier)
                {
                    Advance();
                    string prevName = memberName;
                    memberName = Current().Value;
                    Advance();
                    if (Check("("))
                    {
                        statements.Add(new JavaStatementInfo("call", [], prevName, memberName, [], stmtLine, true));
                        SkipParens();
                        stmtLine = CurrentLine();
                        memberName = "";
                    }
                }

                if (memberName != "" && Check("("))
                {
                    statements.Add(new JavaStatementInfo("call", [], null, memberName, [], stmtLine, true));
                    SkipParens();
                }
                continue;
            }

            // new keyword
            if (CheckKeyword("new"))
            {
                int stmtLine = CurrentLine();
                Advance();
                string ctorType = ConsumeQualifiedName();
                SkipGenerics();
                if (Check("("))
                {
                    statements.Add(new JavaStatementInfo("call", [], ctorType, "<init>", [], stmtLine, true));
                    SkipParens();
                }
                continue;
            }

            Advance();
        }
    }

    private Modifier ParseModifiers(out bool isSealed, out bool isNonSealed, out bool isFinal)
        => ParseModifiers(
            out isSealed,
            out isNonSealed,
            out isFinal,
            out _,
            out _,
            out _,
            out _);

    private Modifier ParseModifiers(
        out bool isSealed,
        out bool isNonSealed,
        out bool isFinal,
        out bool isSynchronized,
        out bool isNative,
        out bool isDefault,
        out bool isStrictfp)
    {
        isSealed = false;
        isNonSealed = false;
        isFinal = false;
        isSynchronized = false;
        isNative = false;
        isDefault = false;
        isStrictfp = false;
        var mod = Modifier.None;
        while (!IsAtEnd())
        {
            if (MatchKeyword("public")) mod |= Modifier.Public;
            else if (MatchKeyword("private")) mod |= Modifier.Private;
            else if (MatchKeyword("protected")) mod |= Modifier.Protected;
            else if (MatchKeyword("static")) mod |= Modifier.Static;
            else if (MatchKeyword("abstract")) mod |= Modifier.Abstract;
            else if (MatchKeyword("final"))
            {
                mod |= Modifier.Sealed;
                isFinal = true;
            }
            else if (MatchKeyword("synchronized")) { isSynchronized = true; }
            else if (MatchKeyword("native")) { isNative = true; }
            else if (MatchKeyword("volatile")) { }
            else if (MatchKeyword("transient")) { }
            else if (MatchKeyword("strictfp")) { isStrictfp = true; }
            else if (MatchKeyword("default")) { isDefault = true; }
            else if (MatchKeyword("sealed")) { isSealed = true; }
            else if (MatchKeyword("non-sealed")) { isNonSealed = true; }
            else break;
        }
        return mod;
    }

    private void SkipAnnotations()
    {
        while (!IsAtEnd() && Current().Kind == JavaTokenKind.Annotation)
            Advance();
    }

    private List<string> CollectAnnotations()
    {
        var list = new List<string>();
        while (!IsAtEnd() && Current().Kind == JavaTokenKind.Annotation)
        {
            string v = Current().Value;
            if (v.StartsWith('@')) v = v[1..];
            int paren = v.IndexOf('(');
            if (paren > 0) v = v[..paren];
            list.Add(v);
            Advance();
        }
        return list;
    }

    private void SkipThrows()
    {
        if (MatchKeyword("throws"))
        {
            while (!IsAtEnd() && !Check("{") && !Check(";"))
            {
                Advance();
            }
        }
    }

    #region Token helpers

    private JavaToken Current() => _pos < tokens.Count ? tokens[_pos] : tokens[^1];
    private JavaToken Peek() => _pos + 1 < tokens.Count ? tokens[_pos + 1] : tokens[^1];
    private int CurrentLine() => Current().Line;
    private bool IsAtEnd() => _pos >= tokens.Count || Current().Kind == JavaTokenKind.Eof;
    private object? Advance() { if (!IsAtEnd()) _pos++; return null; }

    private bool Check(string value) => !IsAtEnd() && Current().Value == value;
    private bool CheckKeyword(string kw) => !IsAtEnd() && Current().Kind == JavaTokenKind.Keyword && Current().Value == kw;
    private bool MatchKeyword(string kw) { if (CheckKeyword(kw)) { Advance(); return true; } return false; }

    private string ConsumeIdentifier()
    {
        if (!IsAtEnd() && (Current().Kind == JavaTokenKind.Identifier
            || (Current().Kind == JavaTokenKind.Keyword && ContextualKeywords.Contains(Current().Value))))
        { var v = Current().Value; Advance(); return v; }
        return "";
    }

    // Java contextual keywords that are also legal identifiers (param/var/method names).
    // Treating them as hard keywords caused the parser to stall on e.g. `Object record`.
    private static readonly HashSet<string> ContextualKeywords =
        ["var", "yield", "record", "sealed", "permits", "non-sealed"];

    private string ConsumeQualifiedName()
    {
        var parts = new List<string>();
        while (!IsAtEnd())
        {
            if (Current().Kind is JavaTokenKind.Identifier or JavaTokenKind.Keyword)
            {
                parts.Add(Current().Value);
                Advance();
                if (Check("."))
                {
                    if (Peek().Kind is JavaTokenKind.Identifier or JavaTokenKind.Keyword)
                    { parts.Add("."); Advance(); }
                    else if (Peek().Value == "*") // wildcard import: java.util.*
                    { parts.Add(".*"); Advance(); Advance(); break; }
                    else break;
                }
                else break;
            }
            else if (Check("*")) { parts.Add("*"); Advance(); break; }
            else break;
        }
        return string.Join("", parts);
    }

    private string ConsumeTypeName()
    {
        var parts = new List<string>();
        while (!IsAtEnd() && (Current().Kind is JavaTokenKind.Identifier or JavaTokenKind.Keyword))
        {
            // Primitive keywords like int, byte, void etc.
            if (Current().Kind == JavaTokenKind.Keyword && Current().Value is not "void" and not "int" and not "long" and not "short" and not "byte" and not "char" and not "float" and not "double" and not "boolean")
                break;
            parts.Add(Current().Value);
            Advance();
            SkipGenerics();
            if (Check(".") && Peek().Kind is JavaTokenKind.Identifier or JavaTokenKind.Keyword)
            { parts.Add("."); Advance(); }
            else break;
        }
        if (Check("[")) { Advance(); if (Check("]")) { parts.Add("[]"); Advance(); } }
        return string.Join("", parts);
    }

    private void SkipGenerics()
    {
        if (!Check("<")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("<")) { depth++; Advance(); }
            else if (Check(">")) { depth--; Advance(); if (depth == 0) break; }
            else if (Check(">>")) { depth -= 2; Advance(); if (depth <= 0) break; }
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

    private bool SkipParensContainsTopLevelColon()
    {
        if (!Check("(")) return false;
        int depth = 0;
        bool containsColon = false;
        while (!IsAtEnd())
        {
            if (Check("("))
            {
                depth++;
                Advance();
            }
            else if (Check(")"))
            {
                depth--;
                Advance();
                if (depth == 0) break;
            }
            else
            {
                if (depth == 1 && Check(":"))
                    containsColon = true;
                Advance();
            }
        }
        return containsColon;
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

    private void SkipUntilSemiOrBrace()
    {
        while (!IsAtEnd() && !Check(";") && !Check("{") && !Check("}"))
            Advance();
        if (Check(";")) Advance();
        else if (Check("{")) SkipBraces();
    }

    private void SkipComments()
    {
        while (!IsAtEnd() && Current().Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment)
            Advance();
    }

    private bool HasPrecedingDocComment()
    {
        for (int i = _pos - 1; i >= 0 && i >= _pos - 10; i--)
        {
            if (tokens[i].Kind == JavaTokenKind.DocComment) return true;
            if (tokens[i].Kind is JavaTokenKind.Annotation or JavaTokenKind.LineComment or JavaTokenKind.BlockComment) continue;
            break;
        }
        return false;
    }

    private HashSet<int> ExtractCommentLines()
    {
        var lines = new HashSet<int>();
        foreach (var t in tokens)
            if (t.Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment)
                lines.Add(t.Line);
        return lines;
    }

    // ── Catch / throw semantic helpers ───────────────────────────────────────────

    /// <summary>
    /// Parses a catch clause <c>(ExType var)</c> or multi-catch <c>(A | B var)</c>.
    /// Advances past the closing <c>)</c>. Returns (firstType, varName, isGenericError).
    /// </summary>
    private (string? exceptionType, string? catchVar, bool isGenericError) ParseCatchClause()
    {
        if (!Check("(")) return (null, null, false);
        Advance(); // skip (

        var types = new List<string>();
        string? catchVar = null;

        while (!IsAtEnd() && !Check(")"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check(")")) break;
            if (MatchKeyword("final")) continue;

            string t = ConsumeTypeName();
            if (t == "") { if (!Check("|") && !Check(")")) Advance(); Guard(g, "catch-clause"); continue; }
            types.Add(t);
            SkipGenerics();

            if (Check("|")) { Advance(); continue; } // multi-catch separator

            // After the type(s) comes the catch variable name
            if (Current().Kind == JavaTokenKind.Identifier)
            {
                catchVar = Current().Value;
                Advance();
            }
            Guard(g, "catch-clause");
        }

        if (Check(")")) Advance();

        string? exceptionType = types.Count > 0 ? types[0] : null;
        bool isGeneric = types.Any(static t => t is "Exception" or "Throwable" or "Error"
            || t.EndsWith(".Exception") || t.EndsWith(".Throwable") || t.EndsWith(".Error"));
        return (exceptionType, catchVar, isGeneric);
    }

    /// <summary>
    /// Peeks into the catch body <c>{ ... }</c> (without consuming) to detect
    /// a bare rethrow: <c>throw varName;</c> at the top level of that body where
    /// <paramref name="catchVar"/> is the bound exception variable.
    /// </summary>
    private bool LookaheadForRethrow(string? catchVar)
    {
        if (!Check("{")) return false;
        int savedPos = _pos;
        int depth = 0;
        bool found = false;

        while (!IsAtEnd())
        {
            if (Check("{")) { depth++; _pos++; }
            else if (Check("}"))
            {
                depth--;
                _pos++;
                if (depth == 0) break;
            }
            else if (depth == 1 && CheckKeyword("throw"))
            {
                _pos++; // consume "throw"
                // bare throw; (not valid Java but defensive)
                if (Check(";")) { found = true; break; }
                // throw varName; → rethrow when varName matches the catch variable
                if (!CheckKeyword("new") && Current().Kind == JavaTokenKind.Identifier)
                {
                    string varName = Current().Value;
                    _pos++;
                    if (Check(";") && (catchVar == null || varName == catchVar))
                    {
                        found = true;
                        break;
                    }
                }
            }
            else _pos++;
        }

        _pos = savedPos;
        return found;
    }

    // ── Error reporting ───────────────────────────────────────────────────────────

    private void AddError(int line, int col, string message)
        => _parseErrors.Add($"{_filePath}({line},{col}): error: {message}");

    /// <summary>
    /// Verifies that every <c>{</c> token has a matching <c>}</c> and vice-versa.
    /// Reports an error for each mismatch. Skips tokens that are not Punctuation
    /// (strings, comments, annotations already consumed their internal braces).
    /// </summary>
    private void CheckBraceBalance()
    {
        var stack = new Stack<JavaToken>();
        foreach (var token in tokens)
        {
            if (token.Kind == JavaTokenKind.Eof) break;
            if (token.Kind != JavaTokenKind.Punctuation) continue;
            if (token.Value == "{") stack.Push(token);
            else if (token.Value == "}")
            {
                if (stack.Count == 0)
                    AddError(token.Line, 1, "Unexpected '}' - no matching '{'");
                else
                    stack.Pop();
            }
        }
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            AddError(t.Line, 1, "Unclosed '{'");
        }
    }

    #endregion
}

#endregion
