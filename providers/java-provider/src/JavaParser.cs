// Tokenizes Java source and projects its declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal class JavaParser(List<JavaToken> tokens, IReadOnlyList<(int line, int col, string msg)> lexerErrors, string sourceText)
{
    private int _pos;
    private string _filePath = string.Empty;
    private readonly List<string> _parseErrors = [];
    private static bool _diag => Cop.Core.CopDiagnostics.IsTimingEnabled;

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
            {
                Console.Error.WriteLine($"[java-parser] non-advancing loop in {where} at token #{_pos} '{Current().Value}' ({Current().Kind}); forcing advance");
            }

            Advance();
        }
        return _pos;
    }

    /// <summary>Projects tokenized Java source into a file-scoped code model.</summary>
    public SourceFile Parse(string filePath)
    {
        _filePath = filePath;
        foreach ((int line, int col, string? msg) in lexerErrors)
        {
            _parseErrors.Add($"{filePath}({line},{col}): error: {msg}");
        }

        List<TypeDeclaration> types = new List<TypeDeclaration>();
        List<StatementInfo> statements = new List<StatementInfo>();
        List<string> usings = new List<string>();
        List<ImportInfo> imports = new List<ImportInfo>();
        string? ns = null;

        while (!IsAtEnd())
        {
            SkipComments();
            if (IsAtEnd())
            {
                break;
            }

            if (MatchKeyword("package"))
            {
                ns = ConsumeQualifiedName();
                if (Check(";"))
                {
                    Advance();
                }
            }
            else if (MatchKeyword("import"))
            {
                bool isStatic = MatchKeyword("static");
                int importLine = CurrentLine();
                string module = ConsumeQualifiedName();
                usings.Add(module);
                imports.Add(new ImportInfo(module, importLine) { IsStatic = isStatic, IsWildcard = module.EndsWith(".*") });
                if (Check(";"))
                {
                    Advance();
                }
            }
            else if (IsTypeStart())
            {
                TypeDeclaration? type = ParseType(statements);
                if (type != null)
                {
                    types.Add(type);
                }
            }
            else
            {
                Advance();
            }
        }

        // Only check brace balance when the lexer found no unterminated constructs
        // (lexer errors may truncate the token stream and cause spurious brace mismatches).
        if (lexerErrors.Count == 0)
        {
            CheckBraceBalance();
        }

        return new SourceFile(filePath, "java", types, statements, sourceText)
        {
            Namespace = ns,
            Usings = usings,
            Imports = imports,
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
        {
            Advance();
        }

        bool isType = CheckKeyword("class") || CheckKeyword("interface") || CheckKeyword("enum") || CheckKeyword("record");
        _pos = saved;
        return isType;
    }

    private TypeDeclaration? ParseType(List<StatementInfo> allStatements)
    {
        List<string> annotations = CollectAnnotations();
        bool hasDoc = HasPrecedingDocComment();
        Modifier modifiers = ParseModifiers(out bool isSealed, out bool isNonSealed, out bool isFinal);
        SkipComments();

        TypeKind kind;
        if (MatchKeyword("class"))
        {
            kind = TypeKind.Class;
        }
        else if (MatchKeyword("interface"))
        {
            kind = TypeKind.Interface;
        }
        else if (MatchKeyword("enum"))
        {
            kind = TypeKind.Enum;
        }
        else if (MatchKeyword("record"))
        {
            kind = TypeKind.Struct; // records as struct
        }
        else
        {
            return null;
        }

        int line = CurrentLine();
        string name = ConsumeIdentifier();
        if (name == string.Empty)
        {
            AddError(CurrentLine(), 1, "Expected type name");
            return null;
        }
        bool isGeneric = Check("<");
        List<string> typeParameters = ParseTypeParameters();

        // Record components
        List<FieldDeclaration> fields = new List<FieldDeclaration>();
        if (kind == TypeKind.Struct && Check("("))
        {
            fields = ParseRecordComponents();
        }

        // extends/implements
        List<string> baseTypes = new List<string>();
        if (MatchKeyword("extends"))
        {
            baseTypes.Add(ConsumeTypeName());
            while (Check(",")) { Advance(); baseTypes.Add(ConsumeTypeName()); }
        }
        if (MatchKeyword("implements"))
        {
            baseTypes.Add(ConsumeTypeName());
            while (Check(",")) { Advance(); baseTypes.Add(ConsumeTypeName()); }
        }
        if (MatchKeyword("permits"))
        {
            ConsumeTypeName();
            while (Check(",")) { Advance(); ConsumeTypeName(); }
        }

        // Body
        List<MethodDeclaration> methods = new List<MethodDeclaration>();
        List<MethodDeclaration> constructors = new List<MethodDeclaration>();
        List<TypeDeclaration> nestedTypes = new List<TypeDeclaration>();
        List<string> enumValues = new List<string>();

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
                if (Check("}"))
                {
                    break;
                }

                if (IsTypeStart())
                {
                    TypeDeclaration? nested = ParseType(allStatements);
                    if (nested != null)
                    {
                        nestedTypes.Add(nested);
                    }
                }
                else
                {
                    MethodDeclaration? member = ParseMember(name, allStatements, fields);
                    if (member != null)
                    {
                        if (member.Name == name || member.Name == "<init>")
                        {
                            constructors.Add(member);
                        }
                        else
                        {
                            methods.Add(member);
                        }
                    }
                }
                Guard(g, "class-body");
            }
            if (Check("}"))
            {
                Advance();
            }
        }

        return new JavaTypeDeclaration(new TypeDeclaration(name, kind, modifiers, baseTypes, annotations, constructors, methods, nestedTypes, enumValues, line)
        { HasDocComment = hasDoc, Fields = fields, TypeParameters = typeParameters }
        )
        {
            IsRecord = kind == TypeKind.Struct,
            IsEnum = kind == TypeKind.Enum,
            HasSealedModifier = isSealed,
            IsNonSealed = isNonSealed,
            IsFinal = isFinal,
            IsGeneric = isGeneric
        };
    }

    private void ParseEnumConstants(List<string> enumValues)
    {
        while (!IsAtEnd() && !Check("}") && !Check(";"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check("}") || Check(";"))
            {
                break;
            }

            if (Current().Kind == JavaTokenKind.Identifier)
            {
                enumValues.Add(Current().Value);
                Advance();
                if (Check("("))
                {
                    SkipParens();
                }

                if (Check("{"))
                {
                    SkipBraces();
                }
            }
            if (Check(","))
            {
                Advance();
            }

            Guard(g, "enum-constants");
        }
        if (Check(";"))
        {
            Advance();
        }
    }

    private List<FieldDeclaration> ParseRecordComponents()
    {
        List<FieldDeclaration> fields = new List<FieldDeclaration>();
        Advance(); // skip (
        while (!IsAtEnd() && !Check(")"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check(")"))
            {
                break;
            }

            string paramType = ConsumeTypeName();
            string paramName = ConsumeIdentifier();
            if (paramName != string.Empty)
            {
                fields.Add(new FieldDeclaration(paramName, new TypeReference(paramType, null, [], paramType), Modifier.Public, CurrentLine()));
            }

            if (Check(","))
            {
                Advance();
            }

            Guard(g, "record-components");
        }
        if (Check(")"))
        {
            Advance();
        }

        return fields;
    }

    private MethodDeclaration? ParseMember(string typeName, List<StatementInfo> allStatements, List<FieldDeclaration> fields)
    {
        SkipComments();
        int memberLine = CurrentLine();
        bool hasDoc = HasPrecedingDocComment();
        List<string> annotations = CollectAnnotations();
        Modifier modifiers = ParseModifiers(
            out _, out _, out _,
            out bool isSynchronized,
            out bool isNative,
            out bool isDefault,
            out bool isStrictfp);

        // Skip type parameters on methods
        bool isGeneric = Check("<");
        List<string> typeParameters = ParseTypeParameters();
        SkipComments();

        if (IsAtEnd() || Check("}"))
        {
            return null;
        }

        // Static/instance initializer block
        if (Check("{"))
        {
            List<StatementInfo> initStatements = new List<StatementInfo>();
            ParseBlock(initStatements);
            allStatements.AddRange(initStatements);
            return null;
        }

        // Determine if this is a constructor (name matches type name and no return type)
        // or a method/field
        string firstType = ConsumeTypeName();
        if (firstType == string.Empty) { Advance(); return null; }

        // Constructor: TypeName(...)
        if (firstType == typeName && Check("("))
        {
            List<ParameterDeclaration> parameters = ParseParameters();
            SkipThrows();
            List<StatementInfo> body = new List<StatementInfo>();
            if (Check("{"))
            {
                ParseBlock(body);
            }

            allStatements.AddRange(body);
            JavaMethodDeclaration constructor = new JavaMethodDeclaration(new MethodDeclaration(
                "<init>",
                modifiers,
                annotations,
                null,
                parameters,
                memberLine))
            {
                Statements = body, HasDocComment = hasDoc, TypeParameters = typeParameters,
                IsSynchronized = isSynchronized, IsNative = isNative, IsDefault = isDefault,
                IsStrictfp = isStrictfp, IsGeneric = isGeneric
            };
            foreach (StatementInfo statement in body)
            {
                statement.Method = constructor;
            }

            return constructor;
        }

        // Could be a field or method
        SkipGenerics();
        if (Check("[")) { Advance(); if (Check("]"))
            {
                Advance();
            }
        } // array type

        if (Current().Kind != JavaTokenKind.Identifier
            && !(Current().Kind == JavaTokenKind.Keyword && s_contextualKeywords.Contains(Current().Value)))
        {
            // Not a valid member — skip to next semicolon or brace
            SkipUntilSemiOrBrace();
            return null;
        }

        string memberName = ConsumeIdentifier();

        // Method
        if (Check("("))
        {
            List<ParameterDeclaration> parameters = ParseParameters();
            SkipThrows();
            TypeReference? returnType = new TypeReference(firstType, null, [], firstType);
            List<StatementInfo> body = new List<StatementInfo>();
            if (Check("{"))
            {
                ParseBlock(body);
            }
            else if (Check(";"))
            {
                Advance();
            }

            allStatements.AddRange(body);
            JavaMethodDeclaration method = new JavaMethodDeclaration(new MethodDeclaration(
                memberName,
                modifiers,
                annotations,
                returnType,
                parameters,
                memberLine))
            {
                Statements = body, HasDocComment = hasDoc, TypeParameters = typeParameters,
                IsSynchronized = isSynchronized, IsNative = isNative, IsDefault = isDefault,
                IsStrictfp = isStrictfp, IsGeneric = isGeneric
            };
            foreach (StatementInfo statement in body)
            {
                statement.Method = method;
            }

            return method;
        }

        // Field
        int fieldLine = memberLine;
        fields.Add(new FieldDeclaration(memberName, new TypeReference(firstType, null, [], firstType), modifiers, fieldLine));
        int initializerStart = _pos;
        // Skip to ;
        while (!IsAtEnd() && !Check(";") && !Check("}"))
        {
            if (Check(",") && Peek().Kind == JavaTokenKind.Identifier)
            {
                Advance();
                int nextLine = CurrentLine();
                fields.Add(new FieldDeclaration(ConsumeIdentifier(), new TypeReference(firstType, null, [], firstType), modifiers, nextLine));
                continue;
            }
            if (Check("("))
            {
                SkipParens();
            }
            else if (Check("{"))
            {
                SkipBraces(); // array initializer
            }
            else
            {
                Advance();
            }
        }
        List<StatementInfo> initializerStatements = new List<StatementInfo>();
        JavaStructuralFacts.Enrich(tokens, initializerStart, _pos, initializerStatements, inMethod: false);
        allStatements.AddRange(initializerStatements);
        if (Check(";"))
        {
            Advance();
        }

        return null;
    }

    private List<ParameterDeclaration> ParseParameters()
    {
        List<ParameterDeclaration> parameters = new List<ParameterDeclaration>();
        if (!Check("("))
        {
            return parameters;
        }

        Advance();
        while (!IsAtEnd() && !Check(")"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check(")"))
            {
                break;
            }

            int parameterLine = CurrentLine();
            if (MatchKeyword("final")) { }
            string paramType = ConsumeTypeName();
            SkipGenerics();
            if (Check("[")) { Advance(); if (Check("]"))
                {
                    Advance();
                }
            }
            bool isVariadic = false;
            if (Check("...")) { Advance(); isVariadic = true; }
            string paramName = ConsumeIdentifier();
            if (paramName != string.Empty)
            {
                parameters.Add(new ParameterDeclaration(paramName, new TypeReference(paramType, null, [], paramType), isVariadic, false, false, parameterLine));
            }

            if (Check(","))
            {
                Advance();
            }

            Guard(g, "parameters");
        }
        if (Check(")"))
        {
            Advance();
        }

        return parameters;
    }

    private void ParseBlock(List<StatementInfo> statements)
    {
        if (!Check("{"))
        {
            return;
        }

        int blockStart = _pos;
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
                statements.Add(new JavaStatementInfo(new StatementInfo("throw", [], excType, null, [], stmtLine, true)));
                continue;
            }

            // assert statement
            if (CheckKeyword("assert"))
            {
                int stmtLine = CurrentLine();
                Advance();
                statements.Add(new JavaStatementInfo(new StatementInfo("assert", [], null, "assert", [], stmtLine, true)));
                continue;
            }

            // catch clause
            if (CheckKeyword("catch"))
            {
                int stmtLine = CurrentLine();
                Advance();
                (string? exceptionType, string? catchVar, bool isGeneric) = ParseCatchClause();
                bool hasRethrow = LookaheadForRethrow(catchVar);
                statements.Add(new JavaStatementInfo(new StatementInfo("catch", [], exceptionType, null, [], stmtLine, true))
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
                if (Check("("))
                {
                    SkipParens();
                }

                statements.Add(new JavaStatementInfo(new StatementInfo("synchronized", [], null, "synchronized", [], stmtLine, true)));
                continue;
            }

            // try / try-with-resources
            if (CheckKeyword("try"))
            {
                int stmtLine = CurrentLine();
                Advance();
                bool isTryWithResources = Check("(");
                if (isTryWithResources)
                {
                    SkipParens();
                }

                statements.Add(new JavaStatementInfo(new StatementInfo("try", [], null, "try", [], stmtLine, true))
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
                statements.Add(new JavaStatementInfo(new StatementInfo("for", [], null, "for", [], stmtLine, true))
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
                        statements.Add(new JavaStatementInfo(new StatementInfo("call", [], prevName, memberName, [], stmtLine, true)));
                        SkipParens();
                        stmtLine = CurrentLine();
                        memberName = string.Empty;
                    }
                }

                if (memberName != string.Empty && Check("("))
                {
                    statements.Add(new JavaStatementInfo(new StatementInfo("call", [], null, memberName, [], stmtLine, true)));
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
                    statements.Add(new JavaStatementInfo(new StatementInfo("call", [], ctorType, "<init>", [], stmtLine, true)));
                    SkipParens();
                }
                continue;
            }

            Advance();
        }
        JavaStructuralFacts.Enrich(tokens, blockStart + 1, _pos, statements);
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
        Modifier mod = Modifier.None;
        while (!IsAtEnd())
        {
            if (MatchKeyword("public"))
            {
                mod |= Modifier.Public;
            }
            else if (MatchKeyword("private"))
            {
                mod |= Modifier.Private;
            }
            else if (MatchKeyword("protected"))
            {
                mod |= Modifier.Protected;
            }
            else if (MatchKeyword("static"))
            {
                mod |= Modifier.Static;
            }
            else if (MatchKeyword("abstract"))
            {
                mod |= Modifier.Abstract;
            }
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
            else
            {
                break;
            }
        }
        return mod;
    }

    private void SkipAnnotations()
    {
        while (!IsAtEnd() && Current().Kind == JavaTokenKind.Annotation)
        {
            Advance();
        }
    }

    private List<string> CollectAnnotations()
    {
        List<string> list = new List<string>();
        while (!IsAtEnd() && Current().Kind == JavaTokenKind.Annotation)
        {
            string v = Current().Value;
            if (v.StartsWith('@'))
            {
                v = v[1..];
            }

            int paren = v.IndexOf('(');
            if (paren > 0)
            {
                v = v[..paren].TrimEnd();
            }

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

    private JavaToken Current() => _pos < tokens.Count ? tokens[_pos] : tokens[^1];
    private JavaToken Peek() => _pos + 1 < tokens.Count ? tokens[_pos + 1] : tokens[^1];
    private int CurrentLine() => Current().Line;
    private bool IsAtEnd() => _pos >= tokens.Count || Current().Kind == JavaTokenKind.Eof;
    private object? Advance() { if (!IsAtEnd()) { _pos++; } return null; }

    private bool Check(string value) => !IsAtEnd() && Current().Value == value;
    private bool CheckKeyword(string kw) => !IsAtEnd() && Current().Kind == JavaTokenKind.Keyword && Current().Value == kw;
    private bool MatchKeyword(string kw) { if (CheckKeyword(kw)) { Advance(); return true; } return false; }

    private string ConsumeIdentifier()
    {
        if (!IsAtEnd() && (Current().Kind == JavaTokenKind.Identifier
            || (Current().Kind == JavaTokenKind.Keyword && s_contextualKeywords.Contains(Current().Value))))
        { string v = Current().Value; Advance(); return v; }
        return string.Empty;
    }

    // Java contextual keywords that are also legal identifiers (param/var/method names).
    // Treating them as hard keywords caused the parser to stall on e.g. `Object record`.
    private static readonly HashSet<string> s_contextualKeywords =
        ["var", "yield", "record", "sealed", "permits", "non-sealed"];

    private string ConsumeQualifiedName()
    {
        List<string> parts = new List<string>();
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
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            else if (Check("*")) { parts.Add("*"); Advance(); break; }
            else
            {
                break;
            }
        }
        return string.Join(string.Empty, parts);
    }

    private string ConsumeTypeName()
    {
        List<string> parts = new List<string>();
        while (!IsAtEnd() && (Current().Kind is JavaTokenKind.Identifier or JavaTokenKind.Keyword))
        {
            // Primitive keywords like int, byte, void etc.
            if (Current().Kind == JavaTokenKind.Keyword && Current().Value is not "void" and not "int" and not "long" and not "short" and not "byte" and not "char" and not "float" and not "double" and not "boolean")
            {
                break;
            }

            parts.Add(Current().Value);
            Advance();
            if (Check("<"))
            {
                int start = _pos;
                SkipGenerics();
                parts.Add(string.Join(string.Empty, tokens[start.._pos].Select(t => t.Value)));
            }
            if (Check(".") && Peek().Kind is JavaTokenKind.Identifier or JavaTokenKind.Keyword)
            { parts.Add("."); Advance(); }
            else
            {
                break;
            }
        }
        if (Check("[")) { Advance(); if (Check("]")) { parts.Add("[]"); Advance(); } }
        return string.Join(string.Empty, parts);
    }

    private void SkipGenerics()
    {
        if (!Check("<"))
        {
            return;
        }

        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("<")) { depth++; Advance(); }
            else if (Check(">")) { depth--; Advance(); if (depth == 0)
                {
                    break;
                }
            }
            else if (Check(">>")) { depth -= 2; Advance(); if (depth <= 0)
                {
                    break;
                }
            }
            else if (Check(">>>")) { depth -= 3; Advance(); if (depth <= 0)
                {
                    break;
                }
            }
            else
            {
                Advance();
            }
        }

    }

    private List<string> ParseTypeParameters()
    {
        int start = _pos;
        SkipGenerics();
        if (_pos == start)
        {
            return [];
        }

        return [string.Join(" ", tokens[(start + 1)..(_pos - 1)].Select(t => t.Value))];
    }

    private void SkipParens()
    {
        if (!Check("("))
        {
            return;
        }

        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("(")) { depth++; Advance(); }
            else if (Check(")")) { depth--; Advance(); if (depth == 0)
                {
                    break;
                }
            }
            else
            {
                Advance();
            }
        }
    }

    private bool SkipParensContainsTopLevelColon()
    {
        if (!Check("("))
        {
            return false;
        }

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
                if (depth == 0)
                {
                    break;
                }
            }
            else
            {
                if (depth == 1 && Check(":"))
                {
                    containsColon = true;
                }

                Advance();
            }
        }
        return containsColon;
    }

    private void SkipBraces()
    {
        if (!Check("{"))
        {
            return;
        }

        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("{")) { depth++; Advance(); }
            else if (Check("}")) { depth--; Advance(); if (depth == 0)
                {
                    break;
                }
            }
            else
            {
                Advance();
            }
        }
    }

    private void SkipUntilSemiOrBrace()
    {
        while (!IsAtEnd() && !Check(";") && !Check("{") && !Check("}"))
        {
            Advance();
        }

        if (Check(";"))
        {
            Advance();
        }
        else if (Check("{"))
        {
            SkipBraces();
        }
    }

    private void SkipComments()
    {
        while (!IsAtEnd() && Current().Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment)
        {
            Advance();
        }
    }

    private bool HasPrecedingDocComment()
    {
        for (int i = _pos - 1; i >= 0 && i >= _pos - 10; i--)
        {
            if (tokens[i].Kind == JavaTokenKind.DocComment)
            {
                return true;
            }

            if (tokens[i].Kind is JavaTokenKind.Annotation or JavaTokenKind.LineComment or JavaTokenKind.BlockComment)
            {
                continue;
            }

            break;
        }
        return false;
    }

    private HashSet<int> ExtractCommentLines()
    {
        HashSet<int> lines = new HashSet<int>();
        foreach (JavaToken t in tokens)
        {
            if (t.Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment)
            {
                for (int line = t.Line; line <= t.Line + t.Value.Count(c => c == '\n'); line++)
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }

    // ── Catch / throw semantic helpers ───────────────────────────────────────────

    /// <summary>
    /// Parses a catch clause <c>(ExType var)</c> or multi-catch <c>(A | B var)</c>.
    /// Advances past the closing <c>)</c>. Returns (firstType, varName, isGenericError).
    /// </summary>
    private (string? exceptionType, string? catchVar, bool isGenericError) ParseCatchClause()
    {
        if (!Check("("))
        {
            return (null, null, false);
        }

        Advance(); // skip (

        List<string> types = new List<string>();
        string? catchVar = null;

        while (!IsAtEnd() && !Check(")"))
        {
            int g = _pos;
            SkipComments();
            SkipAnnotations();
            if (Check(")"))
            {
                break;
            }

            if (MatchKeyword("final"))
            {
                continue;
            }

            string t = ConsumeTypeName();
            if (t == string.Empty) { if (!Check("|") && !Check(")")) { Advance(); } Guard(g, "catch-clause"); continue; }
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

        if (Check(")"))
        {
            Advance();
        }

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
        if (!Check("{"))
        {
            return false;
        }

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
                if (depth == 0)
                {
                    break;
                }
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
            else
            {
                _pos++;
            }
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
        Stack<JavaToken> stack = new Stack<JavaToken>();
        foreach (JavaToken token in tokens)
        {
            if (token.Kind == JavaTokenKind.Eof)
            {
                break;
            }

            if (token.Kind != JavaTokenKind.Punctuation)
            {
                continue;
            }

            if (token.Value == "{")
            {
                stack.Push(token);
            }
            else if (token.Value == "}")
            {
                if (stack.Count == 0)
                {
                    AddError(token.Line, 1, "Unexpected '}' - no matching '{'");
                }
                else
                {
                    stack.Pop();
                }
            }
        }
        while (stack.Count > 0)
        {
            JavaToken t = stack.Pop();
            AddError(t.Line, 1, "Unclosed '{'");
        }
    }

    }
