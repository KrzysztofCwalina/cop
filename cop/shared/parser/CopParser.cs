namespace Cop.Lang.Parser;

using Cop.Lang.Ast;

/// <summary>
/// Clean recursive-descent parser that produces AST nodes.
/// Uses Pratt parsing (precedence climbing) for expressions.
/// Has zero domain knowledge — operates purely on syntax.
/// </summary>
public class CopParser
{
    private readonly List<Token> _tokens;
    private readonly string _filePath;
    private readonly string? _source;
    private int _pos;
    private bool _suppressFilterColon; // true inside ternary then-branch to prevent `:` being consumed as filter

    public CopParser(List<Token> tokens, string filePath = "<unknown>", string? source = null)
    {
        _tokens = tokens;
        _filePath = filePath;
        _source = source;
    }

    public static ModuleNode Parse(string source, string? filePath = null)
    {
        filePath ??= "<unknown>";
        var tokenizer = new Tokenizer(source, filePath);
        var tokens = tokenizer.Tokenize();
        var parser = new CopParser(tokens, filePath, source);
        return parser.ParseModule();
    }

    /// <summary>
    /// Compatibility bridge for legacy ScriptFile consumers.
    /// Converts the new AST parser output into the older ScriptFile shape.
    /// </summary>
    public static Cop.Lang.ScriptFile ParseFile(string source, string? filePath = null)
    {
        var resolvedFilePath = filePath ?? "<unknown>";
        var module = Parse(source, resolvedFilePath);

        var imports = new List<string>();
        var typeDefinitions = new List<Cop.Lang.TypeDefinition>();
        var collectionDeclarations = new List<Cop.Lang.CollectionDeclaration>();
        var letDeclarations = new List<Cop.Lang.LetDeclaration>();
        var predicates = new List<Cop.Lang.PredicateDefinition>();
        var functions = new List<Cop.Lang.FunctionDefinition>();
        var commands = new List<Cop.Lang.CommandBlock>();
        var flagsDefinitions = new List<Cop.Lang.FlagsDefinition>();
        var enumDefinitions = new List<Cop.Lang.EnumDefinition>();

        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case ImportDecl importDecl:
                    imports.Add(importDecl.ModuleName);
                    break;

                case TypeDecl typeDecl:
                    typeDefinitions.Add(ConvertTypeDefinition(typeDecl));
                    break;

                case LetDecl letDecl:
                    if (letDecl.TypeAnnotation?.IsCollection == true)
                    {
                        collectionDeclarations.Add(new Cop.Lang.CollectionDeclaration(
                            letDecl.Name,
                            letDecl.TypeAnnotation.Name,
                            letDecl.Line,
                            letDecl.IsExported,
                            letDecl.DocComment));
                    }

                    letDeclarations.Add(ConvertLetDeclaration(letDecl));
                    break;

                case FunctionDecl functionDecl when IsCommandLike(functionDecl):
                    commands.Add(ConvertCommandBlock(functionDecl));
                    break;

                case FunctionDecl functionDecl when IsPredicateLike(functionDecl):
                    predicates.Add(ConvertPredicateDefinition(functionDecl));
                    break;

                case FunctionDecl functionDecl:
                    functions.Add(ConvertFunctionDefinition(functionDecl));
                    break;

                case EnumDecl enumDecl:
                    enumDefinitions.Add(new Cop.Lang.EnumDefinition(
                        enumDecl.Name,
                        enumDecl.Members,
                        enumDecl.Line,
                        enumDecl.IsExported,
                        enumDecl.DocComment));
                    break;

                case FlagsDecl flagsDecl:
                    flagsDefinitions.Add(new Cop.Lang.FlagsDefinition(
                        flagsDecl.Name,
                        flagsDecl.Members,
                        flagsDecl.Line,
                        flagsDecl.IsExported,
                        flagsDecl.DocComment));
                    break;

                case CommandDecl commandDecl:
                    commands.Add(ConvertCommandBlock(commandDecl));
                    break;
            }
        }

        return new Cop.Lang.ScriptFile(
            resolvedFilePath,
            imports,
            typeDefinitions,
            collectionDeclarations,
            letDeclarations,
            predicates,
            functions,
            commands,
            FlagsDefinitions: flagsDefinitions.Count > 0 ? flagsDefinitions : null,
            EnumDefinitions: enumDefinitions.Count > 0 ? enumDefinitions : null);
    }

    // ========================================================================
    // Module (top-level)
    // ========================================================================

    public ModuleNode ParseModule()
    {
        var declarations = new List<Declaration>();
        while (!IsAtEnd())
        {
            var decl = ParseDeclaration();
            if (decl is not null)
                declarations.Add(decl);
        }
        return new ModuleNode(declarations, 1);
    }

    // ========================================================================
    // Declarations
    // ========================================================================

    private Declaration? ParseDeclaration()
    {
        SkipDocComments(out var docComment);
        if (IsAtEnd()) return null;

        bool isExported = false;
        int line = CurrentLine();

        if (MatchKeyword("export"))
        {
            isExported = true;
            SkipDocComments(out _); // export may be followed by doc comment
        }

        var token = Peek();

        // type declaration
        if (IsKeyword(token, "type"))
            return ParseTypeDecl(isExported, docComment, line);

        // enum declaration
        if (IsKeyword(token, "enum"))
            return ParseEnumDecl(isExported, docComment, line);

        // flags declaration
        if (IsKeyword(token, "flags"))
            return ParseFlagsDecl(isExported, docComment, line);

        // function declaration
        if (IsKeyword(token, "function"))
            return ParseFunctionDecl(isExported, docComment, line);

        // predicate declaration (parsed as a function returning bool)
        if (IsKeyword(token, "predicate"))
            return ParsePredicateAsFunction(isExported, docComment, line);

        // let declaration
        if (IsKeyword(token, "let"))
            return ParseLetDecl(isExported, docComment, line);

        // command declaration
        if (IsKeyword(token, "command"))
            return ParseCommandDecl(isExported, docComment, line);

        // import declaration
        if (IsKeyword(token, "import"))
            return ParseImportDecl(line);

        // async foreach (streaming command sugar)
        if (token.Kind == TokenKind.AsyncKeyword)
        {
            Advance(); // consume 'async'
            if (!Check(TokenKind.ForeachKeyword))
                throw new ParseException("Expected 'foreach' after 'async'", _filePath, CurrentLine(),
                    sourceLine: ParseException.GetSourceLine(_source ?? "", CurrentLine()));
            return ParseForeachAsCommand(isExported, docComment, line, isAsync: true);
        }

        // foreach (top-level command sugar)
        if (IsKeyword(token, "foreach"))
            return ParseForeachAsCommand(isExported, docComment, line);

        // test declaration → parse as command with name
        if (IsKeyword(token, "test"))
            return ParseTestAsCommand(docComment, line);

        // feed declaration → skip (runtime concern)
        if (IsKeyword(token, "feed"))
        {
            SkipToEndOfLine();
            return null;
        }

        // RUN declaration → skip (runtime concern)
        if (IsKeyword(token, "RUN"))
        {
            SkipToEndOfLine();
            return null;
        }

        // A stray closing delimiter or operator at a declaration boundary — an unmatched '}' / ')' /
        // ']', or a dangling ':' / '=>' / ',' / '=' left behind by a malformed expression — is never
        // valid. Fail loudly instead of silently skipping it. Other unrecognized lines are tolerated
        // as bare expressions (the CLI wraps those into an implicit command).
        if (IsStrayTopLevelToken(token))
            throw new ParseException(
                $"Unexpected token '{token.Value}' ({token.Kind})",
                _filePath, CurrentLine(), sourceLine: ParseException.GetSourceLine(_source ?? "", CurrentLine()));

        // Bare expression at top level → expression statement in implicit "main"
        // For now, skip unrecognized lines
        SkipToEndOfLine();
        return null;
    }

    /// <summary>
    /// Tokens that can never legitimately begin a declaration or a bare top-level expression: stray
    /// closing delimiters and dangling separators/operators. Reaching the declaration loop with one
    /// of these means a malformed program (an unmatched brace, an empty filter, a missing '=', ...).
    /// </summary>
    private static bool IsStrayTopLevelToken(Token token) => token.Kind is
        TokenKind.RBrace or TokenKind.RParen or TokenKind.RBracket
        or TokenKind.Colon or TokenKind.Arrow or TokenKind.Comma or TokenKind.Equals;

    private Declaration ParseImportDecl(int line)
    {
        Advance(); // consume 'import'
        var name = ExpectIdentifier("module name");
        return new ImportDecl(name, line);
    }

    private Declaration ParseTypeDecl(bool isExported, string? docComment, int line)
    {
        Advance(); // consume 'type'

        // Handle generic collection type syntax: type [T] = { ... }
        string name;
        if (Check(TokenKind.LBracket))
        {
            Advance(); // consume '['
            var inner = ExpectIdentifier("type parameter");
            Expect(TokenKind.RBracket, "']'");
            name = $"[{inner}]";
        }
        else
        {
            name = ExpectIdentifier("type name");
        }

        // C#-style: type Name : Base, Trait1, Trait2
        string? baseType = null;
        List<string>? traits = null;
        if (Match(TokenKind.Colon))
        {
            // Read first parent (base type or trait)
            string firstParent;
            if (Check(TokenKind.LBracket))
            {
                Advance();
                var inner = ExpectIdentifier("type parameter");
                Expect(TokenKind.RBracket, "']'");
                firstParent = $"[{inner}]";
            }
            else
            {
                firstParent = ExpectIdentifier("base type or trait name");
            }
            baseType = firstParent;

            // Read additional traits after commas
            while (Match(TokenKind.Comma))
            {
                traits ??= [];
                traits.Add(ExpectIdentifier("trait name"));
            }
        }

        var properties = new List<PropertyDecl>();

        // Handle brace-enclosed property block: type Name = { Prop : Type, ... }
        if (Match(TokenKind.Equals))
        {
            if (Match(TokenKind.LBrace))
            {
                ParsePropertyBlock(properties);
                return new TypeDecl(name, baseType, properties, isExported, docComment, line, traits);
            }
            // type Name = BaseType (alias), or an intersection
            // type Name = BaseType & { Prop : Type, ... } (subtype that adds properties).
            baseType = ExpectIdentifier("base type name");
            if (Match(TokenKind.Ampersand))
            {
                Expect(TokenKind.LBrace, "'{'");
                ParsePropertyBlock(properties);
            }
            return new TypeDecl(name, baseType, properties, isExported, docComment, line, traits);
        }

        // Properties follow on subsequent indented lines: Name : Type
        while (!IsAtEnd() && !IsDeclarationStart())
        {
            SkipDocComments(out var propDoc);
            if (IsAtEnd() || IsDeclarationStart()) break;

            var propLine = CurrentLine();
            var propToken = Peek();

            // Property: Name : Type or Name : Type?
            if (propToken.Kind == TokenKind.Identifier)
            {
                var propName = Advance().Value;
                if (Match(TokenKind.Colon))
                {
                    var typeRef = ParseTypeRef();
                    bool isOptional = false;
                    if (Match(TokenKind.QuestionMark))
                        isOptional = true;
                    Match(TokenKind.Comma); // optional trailing comma
                    properties.Add(new PropertyDecl(propName, typeRef, isOptional, propLine));
                }
                else
                {
                    // Not a property, put back and break
                    _pos--;
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return new TypeDecl(name, baseType, properties, isExported, docComment, line, traits);
    }

    /// <summary>
    /// Parses the body of a brace-enclosed property block (the opening '{' is already consumed)
    /// up to and including the closing '}'. Used for both `type X = { ... }` and the
    /// intersection form `type X = Base & { ... }`.
    /// </summary>
    private void ParsePropertyBlock(List<PropertyDecl> properties)
    {
        while (!IsAtEnd() && !Check(TokenKind.RBrace))
        {
            SkipDocComments(out _);
            if (Check(TokenKind.RBrace)) break;
            var propLine = CurrentLine();
            var propName = ExpectIdentifier("property name");

            // Computed property: name => expr
            if (Match(TokenKind.Arrow))
            {
                var expr = ParseExpression();
                Match(TokenKind.Comma); // optional trailing comma
                var computedType = new TypeRef("computed", false, propLine);
                properties.Add(new PropertyDecl(propName, computedType, false, propLine, expr));
                continue;
            }

            Expect(TokenKind.Colon, "':'");
            var typeRef = ParseTypeRef();
            bool isOptional = false;
            if (Match(TokenKind.QuestionMark))
                isOptional = true;
            Match(TokenKind.Comma); // optional trailing comma
            properties.Add(new PropertyDecl(propName, typeRef, isOptional, propLine));
        }
        Expect(TokenKind.RBrace, "'}'");
    }

    private Declaration ParseEnumDecl(bool isExported, string? docComment, int line)
    {
        Advance(); // consume 'enum'
        var name = ExpectIdentifier("enum name");

        TypeRef? memberType = null;
        // Optional type annotation: enum Name : TypeKind = ...
        if (Check(TokenKind.Colon))
        {
            Advance();
            memberType = ParseTypeRef();
        }

        Expect(TokenKind.Equals, "'='");

        var members = new List<string>();
        members.Add(ExpectIdentifierOrString("enum member"));
        while (Match(TokenKind.Pipe))
        {
            members.Add(ExpectIdentifierOrString("enum member"));
        }

        return new EnumDecl(name, memberType, members, isExported, docComment, line);
    }

    private Declaration ParseFlagsDecl(bool isExported, string? docComment, int line)
    {
        Advance(); // consume 'flags'
        var name = ExpectIdentifier("flags name");

        Expect(TokenKind.Equals, "'='");

        var members = new List<string>();
        members.Add(ExpectIdentifier("flags member"));
        while (Match(TokenKind.Pipe))
        {
            members.Add(ExpectIdentifier("flags member"));
        }

        return new FlagsDecl(name, members, isExported, docComment, line);
    }

    private Declaration ParseFunctionDecl(bool isExported, string? docComment, int line)
    {
        Advance(); // consume 'function'
        var name = ExpectIdentifier("function name");

        // Parse parameters: (param1: Type1, param2: Type2)
        var parameters = ParseParameterList();

        // Optional return type: : ReturnType
        TypeRef? returnType = null;
        if (Match(TokenKind.Colon))
        {
            returnType = ParseTypeRef();
        }

        // Optional guard: : (expr) — parenthesized expression after return type
        Expression? guard = null;
        if (Check(TokenKind.Colon) && PeekNext().Kind == TokenKind.LParen)
        {
            Advance(); // consume ':'
            guard = ParseExpression();
        }

        // Body: => expr | = expr | => intrinsic | = intrinsic | = { block } | mapping body
        FunctionBody body;
        if (Match(TokenKind.Arrow) || Match(TokenKind.Equals))
        {
            if (MatchKeyword("intrinsic"))
            {
                body = new IntrinsicBody();
            }
            else if (Check(TokenKind.LBrace) && IsCommandName(name))
            {
                // Block body (only for ALL-UPPERCASE function names)
                body = ParseBlockBody();
            }
            else if (Check(TokenKind.LBrace) && !IsCommandName(name))
            {
                // For lowercase names, { } after = is an object literal expression
                var expr = ParseExpression();
                body = new ExpressionBody(expr);
            }
            else
            {
                var expr = ParseExpression();
                body = new ExpressionBody(expr);
            }
        }
        else if (Check(TokenKind.LBrace) && IsCommandName(name))
        {
            // Block body without = (alternate syntax): function MAIN() { ... }
            body = ParseBlockBody();
        }
        else if (Check(TokenKind.LBrace) && returnType is not null)
        {
            // Braced mapping body after return type: => Violation { Severity = ..., ... }
            // Parse as object literal expression
            var expr = ParseExpression();
            body = new ExpressionBody(expr);
        }
        else
        {
            // Mapping body (field assignments on subsequent lines)
            body = ParseMappingBody();
        }

        return new FunctionDecl(name, parameters, returnType, body, isExported, guard, docComment, line);
    }

    private Declaration ParsePredicateAsFunction(bool isExported, string? docComment, int line)
    {
        Advance(); // consume 'predicate'
        var name = ExpectIdentifier("predicate name");

        // Parse parameters
        var parameters = ParseParameterList();

        // Predicates implicitly return bool, but may have narrowing type: predicate name(T) : NarrowedType
        var returnType = new TypeRef("bool");
        if (Match(TokenKind.Colon))
        {
            // Narrowing type annotation — record it as the return type
            var narrowed = ParseTypeRef();
            returnType = narrowed;
        }

        // Optional guard: : (expr) — parenthesized expression after type
        Expression? guard = null;
        if (Check(TokenKind.Colon) && PeekNext().Kind == TokenKind.LParen)
        {
            Advance(); // consume ':'
            guard = ParseExpression();
        }

        // Body: = expr or => expr
        FunctionBody body;
        if (Match(TokenKind.Equals) || Match(TokenKind.Arrow))
        {
            if (MatchKeyword("intrinsic"))
            {
                body = new IntrinsicBody();
            }
            else
            {
                var expr = ParseExpression();
                body = new ExpressionBody(expr);
            }
        }
        else
        {
            // Constraint-style body (inline expression without =)
            var expr = ParseExpression();
            body = new ExpressionBody(expr);
        }

        return new FunctionDecl(name, parameters, returnType, body, isExported, guard, docComment, line, IsPredicate: true);
    }

    private Declaration ParseLetDecl(bool isExported, string? docComment, int line)
    {
        Advance(); // consume 'let'
        var name = ExpectIdentifier("binding name");

        TypeRef? typeAnnotation = null;
        if (Match(TokenKind.Colon))
        {
            typeAnnotation = ParseTypeRef();
        }

        Expect(TokenKind.Equals, "'='");
        var value = ParseExpression();

        return new LetDecl(name, typeAnnotation, value, isExported, docComment, line);
    }

    private Declaration ParseCommandDecl(bool isExported, string? docComment, int line)
    {
        Advance(); // consume 'command'
        var name = ExpectIdentifier("command name");

        // Uppercase the name — command is sugar for an uppercase block function
        var upperName = name.ToUpperInvariant();

        List<Parameter> parameters = new();
        if (Check(TokenKind.LParen))
        {
            Advance();
            if (!Check(TokenKind.RParen))
            {
                parameters.Add(new Parameter(ExpectIdentifier("parameter"), null));
                while (Match(TokenKind.Comma))
                    parameters.Add(new Parameter(ExpectIdentifier("parameter"), null));
            }
            Expect(TokenKind.RParen, "')'");
        }

        // Command body: = { statements } or = single-statement
        FunctionBody body;
        if (Match(TokenKind.Equals))
        {
            if (Check(TokenKind.LBrace))
            {
                body = ParseBlockBody();
            }
            else
            {
                // Single statement → wrap in block
                var stmt = ParseStatement();
                var stmts = stmt is not null ? new List<Statement> { stmt } : new List<Statement>();
                body = new BlockBody(stmts);
            }
        }
        else if (!IsAtEnd() && !IsDeclarationStart())
        {
            // `command NAME` must be followed by '=' and a body. Anything else left on the line is a
            // syntax error — e.g. `command main print('x')` (the '=' is missing).
            throw new ParseException(
                $"Expected '=' after command name '{name}', but found '{Peek().Value}'",
                _filePath, CurrentLine(), sourceLine: ParseException.GetSourceLine(_source ?? "", CurrentLine()));
        }
        else
        {
            body = new BlockBody(new List<Statement>());
        }

        return new FunctionDecl(upperName, parameters, null, body, isExported, null, docComment, line);
    }

    private Declaration ParseForeachAsCommand(bool isExported, string? docComment, int line, bool isAsync = false)
    {
        // foreach at top-level is sugar for an uppercase command function
        var stmt = ParseForEachStatement(isAsync);
        var body = new BlockBody(new List<Statement> { stmt });
        return new FunctionDecl("__FOREACH__", new List<Parameter>(), null, body, isExported, null, docComment, line);
    }

    private Declaration ParseTestAsCommand(string? docComment, int line)
    {
        Advance(); // consume 'test'
        var name = ExpectIdentifier("test name");

        // Uppercase the name — test is sugar for an uppercase block function
        var upperName = "TEST-" + name.ToUpperInvariant();

        FunctionBody body;
        if (Match(TokenKind.Equals))
        {
            if (Check(TokenKind.LBrace))
            {
                body = ParseBlockBody();
            }
            else
            {
                var stmt = ParseStatement();
                var stmts = stmt is not null ? new List<Statement> { stmt } : new List<Statement>();
                body = new BlockBody(stmts);
            }
        }
        else
        {
            body = new BlockBody(new List<Statement>());
        }

        return new FunctionDecl(upperName, new List<Parameter>(), null, body, false, null, docComment, line);
    }

    // ========================================================================
    // Statements
    // ========================================================================

    private Statement? ParseStatement()
    {
        if (IsAtEnd()) return null;

        var token = Peek();

        if (IsKeyword(token, "let"))
            return ParseLetStatement();

        if (IsKeyword(token, "foreach"))
            return ParseForEachStatement();

        // Expression statement (may include pipeline)
        var expr = ParseExpression();
        if (expr is null) return null;

        // Check for pipeline: expr => expr => expr
        if (Check(TokenKind.Arrow))
        {
            var stages = new List<PipelineStage>();
            while (Match(TokenKind.Arrow))
            {
                var stageExpr = ParseExpression();
                stages.Add(new PipelineStage(stageExpr, stageExpr.Line));
            }
            return new PipelineStatement(expr, stages, expr.Line);
        }

        return new ExpressionStatement(expr, expr.Line);
    }

    private LetStatement ParseLetStatement()
    {
        int line = CurrentLine();
        Advance(); // consume 'let'
        var name = ExpectIdentifier("binding name");

        TypeRef? typeAnnotation = null;
        if (Match(TokenKind.Colon))
            typeAnnotation = ParseTypeRef();

        Expect(TokenKind.Equals, "'='");
        var value = ParseExpression();
        return new LetStatement(name, typeAnnotation, value, line);
    }

    private ForEachStatement ParseForEachStatement(bool isAsync = false)
    {
        int line = CurrentLine();
        Advance(); // consume 'foreach'

        // Collection expression (what we iterate over)
        var collection = ParseExpression();

        // Optional pipeline stages in the foreach
        var body = new List<Statement>();
        while (Match(TokenKind.Arrow))
        {
            var stageExpr = ParseExpression();
            body.Add(new ExpressionStatement(stageExpr, stageExpr.Line));
        }

        return new ForEachStatement("__item__", collection, body, line, isAsync);
    }

    // ========================================================================
    // Expressions — Pratt / Precedence Climbing
    // ========================================================================

    public Expression ParseExpression() => ParseTernary();

    private Expression ParseTernary()
    {
        var expr = ParseOr();

        if (Match(TokenKind.QuestionMark))
        {
            int line = Previous().Line;
            expr = ParseTernaryOrMatch(expr, line);
        }

        return expr;
    }

    private Expression ParseOr()
    {
        var left = ParseAnd();
        while (Match(TokenKind.OrOr))
        {
            int line = Previous().Line;
            var right = ParseAnd();
            left = new BinaryExpr(left, BinaryOp.Or, right, line);
        }
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseEquality();
        while (Match(TokenKind.AndAnd))
        {
            int line = Previous().Line;
            var right = ParseEquality();
            left = new BinaryExpr(left, BinaryOp.And, right, line);
        }
        return left;
    }

    private Expression ParseEquality()
    {
        var left = ParseComparison();
        while (true)
        {
            if (Match(TokenKind.EqualEqual))
            {
                int line = Previous().Line;
                var right = ParseComparison();
                left = new BinaryExpr(left, BinaryOp.Equal, right, line);
            }
            else if (Match(TokenKind.NotEqual))
            {
                int line = Previous().Line;
                var right = ParseComparison();
                left = new BinaryExpr(left, BinaryOp.NotEqual, right, line);
            }
            else break;
        }
        return left;
    }

    private Expression ParseComparison()
    {
        var left = ParseBitwiseOr();
        while (true)
        {
            if (Match(TokenKind.GreaterThan))
            {
                int line = Previous().Line;
                var right = ParseBitwiseOr();
                left = new BinaryExpr(left, BinaryOp.GreaterThan, right, line);
            }
            else if (Match(TokenKind.LessThan))
            {
                int line = Previous().Line;
                var right = ParseBitwiseOr();
                left = new BinaryExpr(left, BinaryOp.LessThan, right, line);
            }
            else if (Match(TokenKind.GreaterEqual))
            {
                int line = Previous().Line;
                var right = ParseBitwiseOr();
                left = new BinaryExpr(left, BinaryOp.GreaterOrEqual, right, line);
            }
            else if (Match(TokenKind.LessEqual))
            {
                int line = Previous().Line;
                var right = ParseBitwiseOr();
                left = new BinaryExpr(left, BinaryOp.LessOrEqual, right, line);
            }
            else break;
        }
        return left;
    }

    private Expression ParseBitwiseOr()
    {
        var left = ParseBitwiseAnd();
        while (Match(TokenKind.Pipe))
        {
            int line = Previous().Line;
            var right = ParseBitwiseAnd();
            left = new BinaryExpr(left, BinaryOp.BitwiseOr, right, line);
        }
        return left;
    }

    private Expression ParseBitwiseAnd()
    {
        var left = ParseAdditive();
        while (Match(TokenKind.Ampersand))
        {
            int line = Previous().Line;
            // `a & foreach xs => body` runs a loop as the right operand (issue #42's `& foreach`
            // chaining). `foreach` is only a loop keyword here — directly after `&` — so its use as
            // an enum-member identifier elsewhere (e.g. `Statement.Kind == foreach`) is unaffected.
            Expression right = IsKeyword(Peek(), "foreach")
                ? new ForEachExpr(ParseForEachStatement(), CurrentLine())
                : ParseAdditive();
            left = new BinaryExpr(left, BinaryOp.BitwiseAnd, right, line);
        }
        return left;
    }

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            if (Match(TokenKind.Plus))
            {
                int line = Previous().Line;
                var right = ParseMultiplicative();
                left = new BinaryExpr(left, BinaryOp.Add, right, line);
            }
            else if (Match(TokenKind.Minus))
            {
                int line = Previous().Line;
                var right = ParseMultiplicative();
                left = new BinaryExpr(left, BinaryOp.Subtract, right, line);
            }
            else break;
        }
        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            if (Match(TokenKind.Star))
            {
                int line = Previous().Line;
                var right = ParseUnary();
                left = new BinaryExpr(left, BinaryOp.Multiply, right, line);
            }
            else if (Match(TokenKind.Slash))
            {
                int line = Previous().Line;
                var right = ParseUnary();
                left = new BinaryExpr(left, BinaryOp.Divide, right, line);
            }
            else if (Match(TokenKind.Percent))
            {
                int line = Previous().Line;
                var right = ParseUnary();
                left = new BinaryExpr(left, BinaryOp.Modulo, right, line);
            }
            else break;
        }
        return left;
    }

    private Expression ParseUnary()
    {
        if (Match(TokenKind.Not))
        {
            int line = Previous().Line;
            var operand = ParseUnary();
            return new UnaryExpr(UnaryOp.Not, operand, line);
        }
        if (Match(TokenKind.Minus))
        {
            int line = Previous().Line;
            var operand = ParseUnary();
            return new UnaryExpr(UnaryOp.Negate, operand, line);
        }
        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
         var expr = ParsePrimary();

        while (true)
        {
            if (Match(TokenKind.Dot))
            {
                int line = Previous().Line;
                var member = ExpectIdentifier("member name");
                expr = new MemberExpr(expr, member, line);

                // Check for call: obj.method(args)
                if (Check(TokenKind.LParen))
                {
                    var args = ParseArgList();
                    expr = new CallExpr(expr, args, line);
                }
            }
            else if (!_suppressFilterColon && Check(TokenKind.Colon) && IsFilterColonFollowed() && IsFilterableExpression(expr))
            {
                Advance(); // consume ':'
                // Filter syntax: expr:predicate or expr:!predicate
                int line = Previous().Line;
                bool negated = Match(TokenKind.Not);
                var predicate = ParsePostfixPredicate();
                expr = new FilterExpr(expr, predicate, negated, line);
            }
            else if (Check(TokenKind.LParen) && expr is IdentifierExpr or MemberExpr)
            {
                // Direct call: func(args)
                int line = CurrentLine();
                var args = ParseArgList();
                expr = new CallExpr(expr, args, line);
            }
            else if (Check(TokenKind.LBrace) && expr is IdentifierExpr typeId && typeId.Name.Length > 0 && char.IsUpper(typeId.Name[0]))
            {
                // Typed object literal: TypeName { Field = value, ... }
                int line = CurrentLine();
                Advance(); // consume '{'
                var fields = new List<FieldInit>();
                if (!Check(TokenKind.RBrace))
                {
                    ParseObjectField(fields, CurrentLine());
                    while (Match(TokenKind.Comma) || (!Check(TokenKind.RBrace) && !IsAtEnd() && IsIdentifierLike(Peek())))
                        ParseObjectField(fields, CurrentLine());
                }
                Expect(TokenKind.RBrace, "'}'");
                expr = new ObjectExpr(typeId.Name, fields, line);
            }
            else if (Match(TokenKind.LBracket))
            {
                // Index: expr[index]
                int line = Previous().Line;
                var index = ParseExpression();
                Expect(TokenKind.RBracket, "']'");
                expr = new IndexExpr(expr, index, line);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private Expression ParsePostfixPredicate()
    {
        // After ':', parse a predicate reference which may be:
        // - identifier:        expr:myPredicate
        // - identifier(args):  expr:startsWith('foo')
        // A trailing `.member` is intentionally NOT consumed here — it binds to the
        // filter *result*, so `coll:pred.Count` parses as `(coll:pred).Count`
        // (handled by the outer postfix loop).
        var ident = ExpectIdentifier("predicate name");
        Expression pred = new IdentifierExpr(ident, CurrentLine());

        // Call with args: pred(args)
        if (Check(TokenKind.LParen))
        {
            var args = ParseArgList();
            pred = new CallExpr(pred, args, CurrentLine());
        }

        return pred;
    }

    private Expression ParseTernaryOrMatch(Expression condition, int line)
    {
        // Check if this is a match expression: condition ? pattern => result | ...
        // Or a simple ternary: condition ? thenExpr | elseExpr (or : elseExpr)
        //
        // Use ParseBitwiseAnd (not ParseOr) for then-branch to avoid consuming '|'
        // as bitwise or when it's meant as the ternary else separator.
        // Suppress filter colon so `cond ? x : y` isn't parsed as `cond ? (x:y)`.

        _suppressFilterColon = true;
        var firstExpr = ParseBitwiseAnd();
        _suppressFilterColon = false;

        if (Match(TokenKind.Arrow))
        {
            // Match expression. Arm RESULTS are parsed with ParseBitwiseAnd so the `|` arm
            // separator is not swallowed as a bitwise-or (which would pull the next arm's `_`
            // pattern into the result expression and evaluate it — issue #40). Wrap a result in
            // parentheses if it genuinely needs `|` or `||`.
            var arms = new List<MatchArm>();
            var firstResult = ParseBitwiseAnd();
            var firstPattern = ExprToPattern(firstExpr);
            arms.Add(new MatchArm(firstPattern, firstResult, line));

            while (Match(TokenKind.Pipe))
            {
                // Parse the arm pattern with ParseBitwiseAnd (like the first arm) so that `_ => x`
                // is NOT consumed as a lambda — `_` must become a WildcardPattern (issue #40).
                _suppressFilterColon = true;
                var patExpr = ParseBitwiseAnd();
                _suppressFilterColon = false;
                Expect(TokenKind.Arrow, "'=>'");
                var result = ParseBitwiseAnd();
                arms.Add(new MatchArm(ExprToPattern(patExpr), result, CurrentLine()));
            }

            return new MatchExpr(condition, arms, line);
        }

        if (Match(TokenKind.Colon) || Match(TokenKind.Pipe))
        {
            // Simple ternary: condition ? then : else  OR  condition ? then | else
            var elseExpr = ParseExpression();
            return new ConditionalExpr(condition, firstExpr, elseExpr, line);
        }

        // Fallback: just ternary with implicit else
        return new ConditionalExpr(condition, firstExpr, new LiteralExpr(null, line), line);
    }

    private Expression ParsePrimary()
    {
        int line = CurrentLine();
        var token = Peek();

        // String literal (may be interpolated)
        if (token.Kind == TokenKind.StringLiteral)
        {
            Advance();
            if (token.Value.Contains('{'))
                return ParseInterpolatedString(token.Value, line);
            return new LiteralExpr(token.Value, line);
        }

        // Verbatim string literal @'...': literal backslashes, never interpolated.
        if (token.Kind == TokenKind.VerbatimStringLiteral)
        {
            Advance();
            return new LiteralExpr(token.Value, line);
        }

        // Integer literal
        if (token.Kind == TokenKind.IntLiteral)
        {
            Advance();
            return new LiteralExpr(int.Parse(token.Value), line);
        }

        // Number literal
        if (token.Kind == TokenKind.NumberLiteral)
        {
            Advance();
            return new LiteralExpr(double.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture), line);
        }

        // Boolean literals
        if (token.Kind == TokenKind.True) { Advance(); return new LiteralExpr(true, line); }
        if (token.Kind == TokenKind.False) { Advance(); return new LiteralExpr(false, line); }

        // Null literal (nic)
        if (token.Kind == TokenKind.Nic) { Advance(); return new LiteralExpr(null, line); }

        // Parenthesized expression or lambda
        if (token.Kind == TokenKind.LParen)
        {
            return ParseParenOrLambda();
        }

        // List literal
        if (token.Kind == TokenKind.LBracket)
        {
            return ParseListLiteral();
        }

        // Object literal
        if (token.Kind == TokenKind.LBrace)
        {
            return ParseObjectLiteral();
        }

        // Identifier (or any keyword used as identifier in expression position)
        if (IsIdentifierLike(token))
        {
            Advance();
            return new IdentifierExpr(token.Value, line);
        }

        throw new ParseException($"Unexpected token '{token.Value}' ({token.Kind})", _filePath, line,
            sourceLine: ParseException.GetSourceLine(_source ?? "", line));
    }

    private Expression ParseParenOrLambda()
    {
        int line = CurrentLine();
        Advance(); // consume '('

        // Empty parens: ()
        if (Match(TokenKind.RParen))
        {
            // This could be a unit value or empty lambda params
            if (Match(TokenKind.Arrow))
            {
                var body = ParseExpression();
                return new LambdaExpr(new List<Parameter>(), body, line);
            }
            return new LiteralExpr(null, line);
        }

        // Try to detect lambda: (param, param) => body
        // Save position for backtracking
        int savedPos = _pos;
        bool isLambda = TryParseLambdaParams(out var lambdaParams);

        if (isLambda && Match(TokenKind.Arrow))
        {
            var body = ParseExpression();
            return new LambdaExpr(lambdaParams!, body, line);
        }

        // Not a lambda, restore and parse as grouped expression
        _pos = savedPos;
        var expr = ParseExpression();
        Expect(TokenKind.RParen, "')'");
        return expr;
    }

    private bool TryParseLambdaParams(out List<Parameter>? parameters)
    {
        parameters = new List<Parameter>();
        int saved = _pos;

        try
        {
            var name = ExpectIdentifier("parameter");
            TypeRef? type = null;
            if (Match(TokenKind.Colon))
                type = ParseTypeRef();
            parameters.Add(new Parameter(name, type));

            while (Match(TokenKind.Comma))
            {
                name = ExpectIdentifier("parameter");
                type = null;
                if (Match(TokenKind.Colon))
                    type = ParseTypeRef();
                parameters.Add(new Parameter(name, type));
            }

            if (!Match(TokenKind.RParen))
            {
                _pos = saved;
                parameters = null;
                return false;
            }

            return true;
        }
        catch
        {
            _pos = saved;
            parameters = null;
            return false;
        }
    }

    private Expression ParseListLiteral()
    {
        int line = CurrentLine();
        Advance(); // consume '['
        var elements = new List<Expression>();

        if (!Check(TokenKind.RBracket))
        {
            elements.Add(ParseExpression());
            // Accept both comma-separated and space-separated elements
            while (!Check(TokenKind.RBracket) && !IsAtEnd())
            {
                Match(TokenKind.Comma); // optional comma
                if (Check(TokenKind.RBracket)) break;
                elements.Add(ParseExpression());
            }
        }

        Expect(TokenKind.RBracket, "']'");
        return new ListExpr(elements, line);
    }

    private Expression ParseObjectLiteral()
    {
        int line = CurrentLine();
        Advance(); // consume '{'
        var fields = new List<FieldInit>();

        if (!Check(TokenKind.RBrace))
        {
            ParseObjectField(fields, line);

            while (Match(TokenKind.Comma)
                || (!Check(TokenKind.RBrace) && !IsAtEnd()
                    && (IsIdentifierLike(Peek()) || Check(TokenKind.StringLiteral))))
            {
                ParseObjectField(fields, CurrentLine());
            }
        }

        Expect(TokenKind.RBrace, "'}'");
        return new ObjectExpr(null, fields, line);
    }

    private void ParseObjectField(List<FieldInit> fields, int line)
    {
        if (Check(TokenKind.RBrace)) return;
        // Accept a quoted key for names with special characters (issue #43): `'content-type' = ...`.
        var fieldName = ExpectIdentifierOrString("field name");
        // Accept both ':' and '=' as field separator
        if (!Match(TokenKind.Colon) && !Match(TokenKind.Equals))
        {
            var errLine = CurrentLine();
            throw new ParseException($"Expected ':' or '=' after field name '{fieldName}'", _filePath, errLine,
                sourceLine: ParseException.GetSourceLine(_source ?? "", errLine));
        }
        var fieldValue = ParseExpression();
        fields.Add(new FieldInit(fieldName, fieldValue, line));
    }

    // ========================================================================
    // Interpolated Strings
    // ========================================================================

    private Expression ParseInterpolatedString(string raw, int line)
    {
        // Parse '{expr}' and '{text@style}' interpolation patterns in a string.
        // Returns InterpolatedStringExpr with TextPart and ExpressionPart segments.
        // Only treat {X} as interpolation if X starts with a letter (valid identifier).
        // This allows regex quantifiers like {2}, {1,3} to remain literal.
        var parts = new List<StringPart>();
        int i = 0;

        while (i < raw.Length)
        {
            if (raw[i] == '{')
            {
                int end = raw.IndexOf('}', i + 1);
                if (end < 0)
                {
                    // Unmatched brace — treat rest as text
                    parts.Add(new TextPart(raw[i..]));
                    break;
                }

                var inner = raw[(i + 1)..end];

                // Regex quantifiers like {2}, {1,3}, {2,} contain only digits/commas/spaces — keep
                // them literal. Anything else (identifiers, operators, calls) is an interpolated
                // expression: `{1 + 2}` → 3, `{xs.Where(item.N > 1).Count}` → count (issue #39).
                bool isRegexQuantifier = inner.Length > 0 && inner.All(c => char.IsDigit(c) || c == ',' || char.IsWhiteSpace(c));
                if (inner.Length == 0 || isRegexQuantifier)
                {
                    // Literal brace content (e.g., {2}, {1,3})
                    parts.Add(new TextPart(raw[i..(end + 1)]));
                    i = end + 1;
                    continue;
                }

                // Check for style syntax: {text@style} — literal text with styling
                // vs expression with styling: {expr.path@style}
                if (inner.Contains('@'))
                {
                    var atIndex = inner.IndexOf('@');
                    var exprPart = inner[..atIndex];
                    var stylePart = inner[(atIndex + 1)..];

                    // A bare word (single identifier) is literal styled text (e.g., {Hello@red});
                    // anything with member access / operators / calls is an expression to evaluate.
                    bool isBareWord = exprPart.Length > 0 && exprPart.All(c => char.IsLetterOrDigit(c) || c == '_');
                    if (isBareWord)
                        parts.Add(new TextPart(exprPart));
                    else
                        parts.Add(new ExpressionPart(ParseEmbeddedExpression(exprPart, line), stylePart));
                }
                else
                {
                    parts.Add(new ExpressionPart(ParseEmbeddedExpression(inner, line)));
                }

                i = end + 1;
            }
            else
            {
                int next = raw.IndexOf('{', i);
                if (next < 0)
                {
                    parts.Add(new TextPart(raw[i..]));
                    break;
                }
                if (next > i)
                    parts.Add(new TextPart(raw[i..next]));
                i = next;
            }
        }

        return new InterpolatedStringExpr(parts, line);
    }

    /// <summary>
    /// Parses an interpolation placeholder's content (e.g. <c>1 + 2</c> or
    /// <c>xs.Where(item.N > 1).Count</c>) as a full expression by tokenizing and parsing it with a
    /// sub-parser. Falls back to a dotted-path parse if it isn't a valid expression, so malformed
    /// placeholders degrade gracefully rather than throwing.
    /// </summary>
    private Expression ParseEmbeddedExpression(string inner, int line)
    {
        try
        {
            var tokens = new Tokenizer(inner, _filePath).Tokenize();
            var sub = new CopParser(tokens, _filePath, inner);
            return sub.ParseExpression();
        }
        catch
        {
            return ParseDottedPath(inner, line);
        }
    }

    private Expression ParseDottedPath(string path, int line)
    {
        // Parse "item.Name" or "x.y.z" into a MemberExpr chain.
        // Trailing "()" on a segment is stripped (e.g., "item.Children.count()" → member "count").
        var segments = path.Split('.');
        Expression expr = new IdentifierExpr(segments[0].Trim(), line);
        for (int i = 1; i < segments.Length; i++)
        {
            var seg = segments[i].Trim();
            if (seg.EndsWith("()"))
                seg = seg[..^2];
            if (seg.Length > 0)
                expr = new MemberExpr(expr, seg, line);
        }
        return expr;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private List<Parameter> ParseParameterList()
    {
        var parameters = new List<Parameter>();
        if (!Match(TokenKind.LParen)) return parameters;

        if (!Check(TokenKind.RParen))
        {
            parameters.Add(ParseParameter());
            while (Match(TokenKind.Comma))
                parameters.Add(ParseParameter());
        }
        Expect(TokenKind.RParen, "')'");
        return parameters;
    }

    private Parameter ParseParameter()
    {
        int line = CurrentLine();
        var first = ExpectIdentifier("parameter name or type");

        // Check if this is "name : Type" or "Type:constraint" or just "Type"
        if (Match(TokenKind.Colon))
        {
            // Look at what follows to decide: is this "name : Type" or "Type:constraint"?
            var nextToken = Peek();

            // If next is an identifier and the token after THAT is ')', ',', or ':' (chained filter),
            // and first starts with uppercase, this might be Type:constraint
            if (char.IsUpper(first[0]) && IsIdentifierLike(nextToken))
            {
                // Peek further: if after the next ident we see '(' or ':' or ')' or ',' 
                // without seeing '=' or '=>', it's a constraint chain, not name:Type
                int savedPos = _pos;
                Advance(); // consume the next ident
                var afterIdent = Peek();
                _pos = savedPos; // restore

                if (afterIdent.Kind == TokenKind.LParen || afterIdent.Kind == TokenKind.Colon
                    || afterIdent.Kind == TokenKind.RParen || afterIdent.Kind == TokenKind.Comma)
                {
                    // This is "Type:constraint" (e.g. `Statement:isCSharp`). Capture the first bare
                    // constraint predicate so guarded overloads can dispatch on it (issue #35);
                    // call-form constraints (`Type:isInRange(0,9)`) are not captured. Then skip any
                    // remaining chained constraints.
                    string? constraint = afterIdent.Kind == TokenKind.LParen ? null : nextToken.Value;
                    SkipConstraintChain();
                    return new Parameter(first, new TypeRef(first, false, line, constraint), line);
                }
            }

            // Standard "name : Type" 
            var type = ParseTypeRef();
            return new Parameter(first, type, line);
        }

        // If it starts with uppercase, it's likely just a type name (anonymous param)
        if (char.IsUpper(first[0]))
        {
            return new Parameter(first, new TypeRef(first, false, line), line);
        }

        // Otherwise it's an untyped parameter name
        return new Parameter(first, null, line);
    }

    private void SkipConstraintChain()
    {
        // Skip tokens until we hit ')', ','  (end of this parameter)
        while (!IsAtEnd() && !Check(TokenKind.RParen) && !Check(TokenKind.Comma))
            Advance();
    }

    private TypeRef ParseTypeRef()
    {
        int line = CurrentLine();
        // [Type] or [Type:Constraint] for collection types
        if (Match(TokenKind.LBracket))
        {
            var innerName = ExpectIdentifier("type name");
            string? constraint = null;
            if (Match(TokenKind.Colon))
                constraint = ExpectIdentifier("constraint name");
            Expect(TokenKind.RBracket, "']'");
            return new TypeRef(innerName, true, line, constraint);
        }
        // { ... } anonymous record type — skip and return "object"
        if (Check(TokenKind.LBrace))
        {
            SkipBalancedBraces();
            return new TypeRef("object", false, line);
        }
        // (Type, ...) => ReturnType — function type signature
        if (Check(TokenKind.LParen))
        {
            return ParseFunctionTypeRef(line);
        }
        var name = ExpectIdentifier("type name");
        return new TypeRef(name, false, line);
    }

    private TypeRef ParseFunctionTypeRef(int line)
    {
        Advance(); // consume '('
        var paramTypes = new List<string>();
        if (!Check(TokenKind.RParen))
        {
            paramTypes.Add(ParseFunctionParam());
            while (Match(TokenKind.Comma))
                paramTypes.Add(ParseFunctionParam());
        }
        Expect(TokenKind.RParen, "')'");
        Expect(TokenKind.Arrow, "'=>'");
        var returnType = ParseTypeRef();
        var signature = $"({string.Join(", ", paramTypes)}) => {FormatTypeRef(returnType)}";
        return new TypeRef(signature, false, line);
    }

    private string ParseFunctionParam()
    {
        // Check for named parameter: name : Type
        if (Check(TokenKind.Identifier) && _pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.Colon)
        {
            var paramName = Advance().Value;
            Advance(); // consume ':'
            var typeRef = ParseTypeRef();
            return $"{paramName}: {FormatTypeRef(typeRef)}";
        }
        return FormatTypeRef(ParseTypeRef());
    }

    private void SkipBalancedBraces()
    {
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check(TokenKind.LBrace)) depth++;
            else if (Check(TokenKind.RBrace)) { depth--; Advance(); if (depth <= 0) return; continue; }
            Advance();
        }
    }

    private MappingBody ParseMappingBody()
    {
        var mappings = new List<FieldMapping>();
        while (!IsAtEnd() && !IsDeclarationStart())
        {
            int line = CurrentLine();
            var token = Peek();
            if (!IsIdentifierLike(token)) break;

            var fieldName = Advance().Value;
            if (!Match(TokenKind.Equals))
            {
                _pos--;
                break;
            }
            var value = ParseExpression();
            mappings.Add(new FieldMapping(fieldName, value, line));
        }
        return new MappingBody(mappings);
    }

    private BlockBody ParseBlockBody()
    {
        Advance(); // consume '{'
        var stmts = new List<Statement>();
        while (!Check(TokenKind.RBrace) && !IsAtEnd())
        {
            var stmt = ParseStatement();
            if (stmt is not null)
                stmts.Add(stmt);
            else
                break;
        }
        Expect(TokenKind.RBrace, "'}'");
        return new BlockBody(stmts);
    }

    /// <summary>
    /// Returns true if the name follows ALL-UPPERCASE convention (command function).
    /// All letter characters must be uppercase. Allows digits, hyphens, underscores.
    /// </summary>
    private static bool IsCommandName(string name)
    {
        foreach (char c in name)
        {
            if (char.IsLetter(c) && !char.IsUpper(c))
                return false;
        }
        return name.Length > 0 && name.Any(char.IsLetter);
    }

    private List<Expression> ParseArgList()
    {
        var args = new List<Expression>();
        Advance(); // consume '('
        if (!Check(TokenKind.RParen))
        {
            args.Add(ParseExpression());
            while (Match(TokenKind.Comma))
                args.Add(ParseExpression());
        }
        Expect(TokenKind.RParen, "')'");
        return args;
    }

    private Pattern ExprToPattern(Expression expr) => expr switch
    {
        LiteralExpr lit => lit.Value is null ? new WildcardPattern(lit.Line) : new LiteralPattern(lit.Value, lit.Line),
        IdentifierExpr id when id.Name == "_" => new WildcardPattern(id.Line),
        IdentifierExpr id => new IdentifierPattern(id.Name, id.Line),
        _ => new LiteralPattern(expr, expr.Line) // fallback
    };

    /// <summary>
    /// Checks if ':' is followed by an identifier or '!' (filter syntax),
    /// as opposed to being used in ternary (cond ? then : else) or type annotations.
    /// </summary>
    private bool IsFilterColonFollowed()
    {
        if (!Check(TokenKind.Colon)) return false;
        int next = _pos + 1;
        if (next >= _tokens.Count) return false;
        var nextToken = _tokens[next];
        // Filter: followed by identifier or ! (negated filter)
        return IsIdentifierLike(nextToken) || nextToken.Kind == TokenKind.Not;
    }

    /// <summary>
    /// Returns true if the expression supports `:` syntax — collection filter or scalar value pipe
    /// (`value:func` → `func(value)`, issue #44). Ternary-else colons (`cond ? 1 : x`) are
    /// disambiguated by <c>_suppressFilterColon</c> (set while parsing the then-branch) and by
    /// <see cref="IsFilterColonFollowed"/> requiring an identifier (not a literal) after ':'.
    /// </summary>
    private static bool IsFilterableExpression(Expression expr) => true;

    private static bool IsCommandLike(FunctionDecl functionDecl)
        => IsCommandName(functionDecl.Name);

    private static bool IsPredicateLike(FunctionDecl functionDecl)
        => functionDecl.IsPredicate
            || (!IsCommandLike(functionDecl)
                && functionDecl.Params.Count == 1
                && functionDecl.Body is ExpressionBody { Expr: not ObjectExpr }
                && (functionDecl.ReturnType is null || FormatTypeRef(functionDecl.ReturnType) == "bool")
                && !(functionDecl.Params[0].Type is { IsCollection: true }));

    private static Cop.Lang.TypeDefinition ConvertTypeDefinition(TypeDecl typeDecl)
        => new(
            typeDecl.Name,
            typeDecl.BaseType,
            typeDecl.Properties.Select(ConvertPropertyDefinition).ToList(),
            typeDecl.Line,
            typeDecl.IsExported,
            typeDecl.DocComment,
            typeDecl.Traits);

    private static Cop.Lang.PropertyDefinition ConvertPropertyDefinition(PropertyDecl propertyDecl)
        => new(
            propertyDecl.Name,
            propertyDecl.Type.Name,
            propertyDecl.IsOptional,
            propertyDecl.Type.IsCollection,
            propertyDecl.Line,
            propertyDecl.ComputedExpr);

    private static Cop.Lang.LetDeclaration ConvertLetDeclaration(LetDecl letDecl)
    {
        string baseCollection = string.Empty;
        List<Cop.Lang.Expression> filters = [];
        string? pathOverride = null;
        Cop.Lang.Expression? exclusions = null;
        Cop.Lang.Expression? valueExpression = null;

        if (IsLegacyLetQuery(letDecl.Value)
            && TryExtractCollectionQuery(letDecl.Value, out baseCollection, out filters, out pathOverride, out exclusions))
        {
            valueExpression = null;
        }
        else
        {
            valueExpression = WrapExpression(letDecl.Value);
        }

        return new Cop.Lang.LetDeclaration(
            letDecl.Name,
            baseCollection,
            filters,
            letDecl.Line,
            letDecl.IsExported,
            valueExpression,
            exclusions,
            null,
            pathOverride,
            letDecl.DocComment,
            null,
            letDecl.TypeAnnotation is not null ? FormatTypeRef(letDecl.TypeAnnotation) : null);
    }

    private static Cop.Lang.PredicateDefinition ConvertPredicateDefinition(FunctionDecl functionDecl)
    {
        var parameter = functionDecl.Params[0];
        var parameterType = parameter.Type is not null ? FormatTypeRef(parameter.Type) : "object";
        var narrowedType = functionDecl.ReturnType is not null
            && !string.Equals(functionDecl.ReturnType.Name, "bool", StringComparison.OrdinalIgnoreCase)
                ? FormatTypeRef(functionDecl.ReturnType)
                : null;

        return new Cop.Lang.PredicateDefinition(
            functionDecl.Name,
            parameterType,
            TryExtractSimpleName(functionDecl.Guard),
            ExtractBodyExpression(functionDecl.Body, functionDecl.Line, true),
            functionDecl.Line,
            functionDecl.IsExported,
            narrowedType,
            functionDecl.DocComment);
    }

    private static Cop.Lang.FunctionDefinition ConvertFunctionDefinition(FunctionDecl functionDecl)
    {
        var inputType = functionDecl.Params.Count > 0 && functionDecl.Params[0].Type is not null
            ? FormatTypeRef(functionDecl.Params[0].Type!)
            : "";

        var inputName = functionDecl.Params.Count > 0 ? functionDecl.Params[0].Name : null;

        var parameters = functionDecl.Params
            .Skip(1)
            .Select(p => new Cop.Lang.FunctionParameter(p.Name, p.Type is not null ? FormatTypeRef(p.Type) : "object"))
            .ToList();

        var fieldMappings = new Dictionary<string, Cop.Lang.Expression>();
        switch (functionDecl.Body)
        {
            case MappingBody mappingBody:
                foreach (var mapping in mappingBody.Mappings)
                    fieldMappings[mapping.FieldName] = WrapExpression(mapping.Value);
                break;

            case ExpressionBody { Expr: ObjectExpr objectExpr }:
                foreach (var field in objectExpr.Fields)
                    fieldMappings[field.Name] = WrapExpression(field.Value);
                break;
        }

        return new Cop.Lang.FunctionDefinition(
            functionDecl.Name,
            inputType,
            functionDecl.ReturnType is not null ? FormatTypeRef(functionDecl.ReturnType) : "",
            parameters,
            fieldMappings,
            functionDecl.Line,
            functionDecl.IsExported,
            functionDecl.Body is ExpressionBody expressionBody ? WrapExpression(expressionBody.Expr) : null,
            functionDecl.Guard is not null ? WrapExpression(functionDecl.Guard) : null,
            functionDecl.DocComment,
            null,
            functionDecl.Body is IntrinsicBody,
            inputName);
    }

    private static Cop.Lang.CommandBlock ConvertCommandBlock(FunctionDecl functionDecl)
    {
        var name = functionDecl.Name;
        string messageTemplate = string.Empty;
        string? collection = null;
        List<Cop.Lang.Expression> filters = [];
        string? pathOverride = null;
        Cop.Lang.Expression? outputExpression = null;
        Cop.Lang.Expression? exclusions = null;

        PopulateCommandShape(functionDecl.Body, ref name, ref messageTemplate, ref collection, filters, ref pathOverride, ref outputExpression, ref exclusions);

        var isTest = name.StartsWith("TEST-", StringComparison.Ordinal);
        var isCommand = !string.Equals(functionDecl.Name, "__FOREACH__", StringComparison.Ordinal) && !isTest;
        var parameters = functionDecl.Params.Count > 0 ? functionDecl.Params.Select(p => p.Name).ToList() : null;

        return new Cop.Lang.CommandBlock(
            name,
            messageTemplate,
            collection,
            filters,
            functionDecl.Line,
            functionDecl.DocComment,
            isCommand,
            functionDecl.IsExported,
            isTest,
            null,
            null,
            functionDecl.Guard is not null ? WrapExpression(functionDecl.Guard) : null,
            null,
            exclusions,
            parameters,
            outputExpression,
            null,
            pathOverride,
            false,
            functionDecl.Body is IntrinsicBody);
    }

    private static Cop.Lang.CommandBlock ConvertCommandBlock(CommandDecl commandDecl)
    {
        var name = commandDecl.Name;
        string messageTemplate = string.Empty;
        string? collection = null;
        List<Cop.Lang.Expression> filters = [];
        string? pathOverride = null;
        Cop.Lang.Expression? outputExpression = null;
        Cop.Lang.Expression? exclusions = null;

        foreach (var statement in commandDecl.Body)
            ApplyCommandStatement(statement, ref name, ref messageTemplate, ref collection, filters, ref pathOverride, ref outputExpression, ref exclusions);

        return new Cop.Lang.CommandBlock(
            name,
            messageTemplate,
            collection,
            filters,
            commandDecl.Line,
            commandDecl.DocComment,
            true,
            commandDecl.IsExported,
            false,
            null,
            null,
            null,
            null,
            exclusions,
            commandDecl.Parameters,
            outputExpression,
            null,
            pathOverride);
    }

    private static void PopulateCommandShape(
        FunctionBody body,
        ref string name,
        ref string messageTemplate,
        ref string? collection,
        List<Cop.Lang.Expression> filters,
        ref string? pathOverride,
        ref Cop.Lang.Expression? outputExpression,
        ref Cop.Lang.Expression? exclusions)
    {
        switch (body)
        {
            case ExpressionBody expressionBody:
                ApplyCommandExpression(expressionBody.Expr, ref messageTemplate, ref collection, filters, ref pathOverride, ref outputExpression, ref exclusions);
                break;

            case BlockBody blockBody:
                foreach (var statement in blockBody.Statements)
                    ApplyCommandStatement(statement, ref name, ref messageTemplate, ref collection, filters, ref pathOverride, ref outputExpression, ref exclusions);
                break;
        }

        if (string.Equals(name, "__FOREACH__", StringComparison.Ordinal) && !string.IsNullOrEmpty(collection))
            name = collection!;
    }

    private static void ApplyCommandStatement(
        Statement statement,
        ref string name,
        ref string messageTemplate,
        ref string? collection,
        List<Cop.Lang.Expression> filters,
        ref string? pathOverride,
        ref Cop.Lang.Expression? outputExpression,
        ref Cop.Lang.Expression? exclusions)
    {
        switch (statement)
        {
            case ForEachStatement forEachStatement:
                if (TryExtractCollectionQuery(forEachStatement.Collection, out var extractedCollection, out var extractedFilters, out var extractedPath, out var extractedExclusions))
                {
                    collection = extractedCollection;
                    filters.AddRange(extractedFilters);
                    pathOverride = extractedPath;
                    exclusions = extractedExclusions;
                }

                if (forEachStatement.Body.Count > 0)
                {
                    ApplyCommandStatement(
                        forEachStatement.Body[^1],
                        ref name,
                        ref messageTemplate,
                        ref collection,
                        filters,
                        ref pathOverride,
                        ref outputExpression,
                        ref exclusions);
                }
                break;

            case ExpressionStatement expressionStatement:
                ApplyCommandExpression(expressionStatement.Expr, ref messageTemplate, ref collection, filters, ref pathOverride, ref outputExpression, ref exclusions);
                break;

            case PipelineStatement pipelineStatement:
                if (collection is null
                    && TryExtractCollectionQuery(pipelineStatement.Source, out var pipelineCollection, out var pipelineFilters, out var pipelinePath, out var pipelineExclusions))
                {
                    collection = pipelineCollection;
                    filters.AddRange(pipelineFilters);
                    pathOverride = pipelinePath;
                    exclusions = pipelineExclusions;
                }

                if (pipelineStatement.Stages.Count > 0)
                    outputExpression = WrapExpression(pipelineStatement.Stages[^1].Expr);
                break;
        }
    }

    private static void ApplyCommandExpression(
        Expression expression,
        ref string messageTemplate,
        ref string? collection,
        List<Cop.Lang.Expression> filters,
        ref string? pathOverride,
        ref Cop.Lang.Expression? outputExpression,
        ref Cop.Lang.Expression? exclusions)
    {
        if (TryExtractStringLiteral(expression, out var literal))
        {
            messageTemplate = literal;
            return;
        }

        if (collection is null
            && TryExtractCollectionQuery(expression, out var extractedCollection, out var extractedFilters, out var extractedPath, out var extractedExclusions))
        {
            collection = extractedCollection;
            filters.AddRange(extractedFilters);
            pathOverride = extractedPath;
            exclusions = extractedExclusions;
            return;
        }

        outputExpression = WrapExpression(expression);
    }

    private static Cop.Lang.Expression ExtractBodyExpression(FunctionBody body, int line, bool defaultValue)
        => body switch
        {
            ExpressionBody expressionBody => WrapExpression(expressionBody.Expr),
            _ => new Cop.Lang.AstExpressionWrapper(new Cop.Lang.Ast.LiteralExpr(defaultValue, line))
        };

    private static bool IsLegacyLetQuery(Expression expression)
        => expression switch
        {
            FilterExpr => true,
            BinaryExpr { Op: BinaryOp.Subtract } subtraction => IsLegacyLetQuery(subtraction.Left),
            _ => false
        };

    private static bool TryExtractCollectionQuery(
        Expression expression,
        out string baseCollection,
        out List<Cop.Lang.Expression> filters,
        out string? pathOverride,
        out Cop.Lang.Expression? exclusions)
    {
        baseCollection = string.Empty;
        filters = [];
        pathOverride = null;
        exclusions = null;

        if (expression is BinaryExpr { Op: BinaryOp.Subtract } subtraction
            && TryExtractCollectionQuery(subtraction.Left, out baseCollection, out filters, out pathOverride, out var nestedExclusions))
        {
            exclusions = nestedExclusions ?? WrapExpression(subtraction.Right);
            return true;
        }

        var collectedFilters = new Stack<Expression>();
        var current = expression;
        while (current is FilterExpr filterExpr)
        {
            var predicate = filterExpr.Negated
                ? new UnaryExpr(UnaryOp.Not, filterExpr.Predicate, filterExpr.Line)
                : filterExpr.Predicate;
            collectedFilters.Push(predicate);
            current = filterExpr.Collection;
        }

        if (!TryExtractCollectionReference(current, out baseCollection, out pathOverride))
            return false;

        while (collectedFilters.Count > 0)
            filters.Add(WrapExpression(collectedFilters.Pop()));

        return true;
    }

    private static bool TryExtractCollectionReference(Expression expression, out string collection, out string? pathOverride)
    {
        collection = string.Empty;
        pathOverride = null;

        if (TryExtractDottedName(expression, out collection))
            return true;

        if (expression is CallExpr { Args.Count: 1 } callExpr
            && TryExtractStringLiteral(callExpr.Args[0], out var path)
            && TryExtractDottedName(callExpr.Callee, out collection))
        {
            pathOverride = path;
            return true;
        }

        return false;
    }

    private static bool TryExtractDottedName(Expression expression, out string name)
    {
        switch (expression)
        {
            case IdentifierExpr identifierExpr:
                name = identifierExpr.Name;
                return true;

            case MemberExpr memberExpr when TryExtractDottedName(memberExpr.Object, out var parentName):
                name = $"{parentName}.{memberExpr.Member}";
                return true;

            default:
                name = string.Empty;
                return false;
        }
    }

    private static bool TryExtractStringLiteral(Expression expression, out string value)
    {
        if (expression is LiteralExpr { Value: string literal })
        {
            value = literal;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? TryExtractSimpleName(Expression? expression)
    {
        if (expression is null)
            return null;

        return TryExtractDottedName(expression, out var name) ? name : null;
    }

    private static string FormatTypeRef(TypeRef typeRef)
        => typeRef.IsCollection
            ? (typeRef.Constraint != null ? $"[{typeRef.Name}:{typeRef.Constraint}]" : $"[{typeRef.Name}]")
            : typeRef.Name;

    private static Cop.Lang.Expression WrapExpression(Expression expression)
        => new Cop.Lang.AstExpressionWrapper(expression);

    // ========================================================================
    // Token Navigation
    // ========================================================================

    private Token Peek() => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
    private Token PeekNext() => _pos + 1 < _tokens.Count ? _tokens[_pos + 1] : _tokens[^1];
    private Token Previous() => _tokens[_pos - 1];
    private int CurrentLine() => Peek().Line;
    private bool IsAtEnd() => Peek().Kind == TokenKind.Eof;

    private Token Advance()
    {
        var token = Peek();
        if (!IsAtEnd()) _pos++;
        return token;
    }

    private bool Check(TokenKind kind) => !IsAtEnd() && Peek().Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (Check(kind)) { Advance(); return true; }
        return false;
    }

    private bool MatchKeyword(string keyword)
    {
        var token = Peek();
        if (IsKeyword(token, keyword)) { Advance(); return true; }
        return false;
    }

    private Token Expect(TokenKind kind, string expected)
    {
        if (Check(kind)) return Advance();
        var line = CurrentLine();
        throw new ParseException($"Expected {expected}, got '{Peek().Value}'", _filePath, line,
            sourceLine: ParseException.GetSourceLine(_source ?? "", line));
    }

    private string ExpectIdentifier(string context)
    {
        var token = Peek();
        if (IsIdentifierLike(token))
        {
            Advance();
            return token.Value;
        }
        var line = CurrentLine();
        throw new ParseException($"Expected {context} (identifier), got '{token.Value}' ({token.Kind})", _filePath, line,
            sourceLine: ParseException.GetSourceLine(_source ?? "", line));
    }

    private string ExpectIdentifierOrString(string context)
    {
        var token = Peek();
        if (token.Kind == TokenKind.StringLiteral || token.Kind == TokenKind.VerbatimStringLiteral
            || token.Kind == TokenKind.IntLiteral
            || token.Kind == TokenKind.NumberLiteral || IsIdentifierLike(token))
        {
            Advance();
            return token.Value;
        }
        var line = CurrentLine();
        throw new ParseException($"Expected {context}, got '{token.Value}'", _filePath, line,
            sourceLine: ParseException.GetSourceLine(_source ?? "", line));
    }

    /// <summary>
    /// Determines if a token can be used as an identifier in expression position.
    /// Domain-specific keywords are treated as plain identifiers by this parser.
    /// </summary>
    private static bool IsIdentifierLike(Token token) => token.Kind switch
    {
        TokenKind.Identifier => true,
        // All domain-specific keywords are just identifiers to this parser
        TokenKind.CollectionKeyword => true,
        TokenKind.PredicateKeyword => true,
        TokenKind.FunctionKeyword => true,
        TokenKind.ForeachKeyword => true,
        TokenKind.TestKeyword => true,
        TokenKind.AsyncKeyword => true,
        TokenKind.RunKeyword => true,
        TokenKind.FeedKeyword => true,
        TokenKind.FlagsKeyword => true,
        TokenKind.EnumKeyword => true,
        TokenKind.IntrinsicKeyword => true,
        TokenKind.TypeKeyword => true,
        TokenKind.ImportKeyword => true,
        TokenKind.LetKeyword => true,
        TokenKind.CommandKeyword => true,
        TokenKind.ExportKeyword => true,
        _ => false
    };

    private static bool IsKeyword(Token token, string keyword)
    {
        // Match both dedicated keyword tokens and identifiers with the keyword value
        if (token.Value == keyword) return true;
        return false;
    }

    private bool IsDeclarationStart()
    {
        var token = Peek();
        return token.Kind == TokenKind.ExportKeyword
            || IsKeyword(token, "type")
            || IsKeyword(token, "enum")
            || IsKeyword(token, "flags")
            || IsKeyword(token, "function")
            || IsKeyword(token, "predicate")
            || IsKeyword(token, "let")
            || IsKeyword(token, "command")
            || IsKeyword(token, "import")

            || IsKeyword(token, "foreach")
            || IsKeyword(token, "test")
            || IsKeyword(token, "feed")
            || IsKeyword(token, "RUN");
    }

    private void SkipDocComments(out string? docComment)
    {
        docComment = null;
        while (Check(TokenKind.DocComment))
        {
            var text = Advance().Value;
            docComment = docComment is null ? text : $"{docComment}\n{text}";
        }
    }

    private void SkipToEndOfLine()
    {
        // Advance past tokens until we reach a token on a different line or EOF
        int line = CurrentLine();
        while (!IsAtEnd() && Peek().Line == line)
            Advance();
    }
}
