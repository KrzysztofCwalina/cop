namespace Cop.Lang;

public class ScriptParser
{
    private readonly List<Token> _tokens;
    private readonly string _filePath;
    private int _pos;
    private bool _skipPipe;

    private ScriptParser(List<Token> tokens, string filePath)
    {
        _tokens = tokens;
        _filePath = filePath;
    }

    public static ScriptFile Parse(string source, string filePath)
    {
        var tokenizer = new Tokenizer(source, filePath);
        var tokens = tokenizer.Tokenize();
        var parser = new ScriptParser(tokens, filePath);
        return parser.ParseFile();
    }

    private Token Current => _tokens[_pos];

    private Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw new ParseException(
                $"Expected {kind} but got {Current.Kind} '{Current.Value}'",
                _filePath, Current.Line);
        return Advance();
    }

    private Token Advance()
    {
        var token = Current;
        if (_pos < _tokens.Count - 1) _pos++;
        return token;
    }

    private ScriptFile ParseFile()
    {
        var imports = new List<string>();
        var feedPaths = new List<string>();
        var typeDefinitions = new List<TypeDefinition>();
        var flagsDefinitions = new List<FlagsDefinition>();
        var collectionDeclarations = new List<CollectionDeclaration>();
        var letDeclarations = new List<LetDeclaration>();
        var predicates = new List<PredicateDefinition>();
        var functions = new List<FunctionDefinition>();
        var commands = new List<CommandBlock>();
        var runInvocations = new List<RunInvocation>();
        string? pendingDocComment = null;

        // Parse feed declarations first (must appear before imports)
        while (Current.Kind == TokenKind.FeedKeyword)
        {
            Advance(); // consume 'feed'
            var path = Expect(TokenKind.StringLiteral).Value;
            feedPaths.Add(path);
        }

        // Parse imports (must appear before predicates/commands)
        while (Current.Kind == TokenKind.ImportKeyword)
        {
            imports.Add(ParseImport());
        }

        while (Current.Kind != TokenKind.Eof)
        {
            if (Current.Kind == TokenKind.DocComment)
            {
                pendingDocComment = CollectDocComment();
            }
            else if (Current.Kind == TokenKind.ExportKeyword)
            {
                pendingDocComment = null;
                Advance(); // consume 'export'
                if (Current.Kind == TokenKind.TypeKeyword)
                    typeDefinitions.Add(ParseTypeDefinition(isExported: true));
                else if (Current.Kind == TokenKind.CollectionKeyword)
                    collectionDeclarations.Add(ParseCollectionDeclaration(isExported: true));
                else if (Current.Kind == TokenKind.LetKeyword)
                    letDeclarations.Add(ParseLetDeclaration(isExported: true));
                else if (Current.Kind == TokenKind.CommandKeyword)
                    commands.AddRange(ParseLetCommandChain(pendingDocComment, isExported: true));
                else if (Current.Kind == TokenKind.PredicateKeyword)
                    predicates.Add(ParsePredicateDefinition(isExported: true));
                else if (Current.Kind == TokenKind.FunctionKeyword)
                    functions.Add(ParseFunctionDefinition(isExported: true));
                else if (Current.Kind == TokenKind.FlagsKeyword)
                    flagsDefinitions.Add(ParseFlagsDefinition(isExported: true));
                else
                    throw new ParseException(
                        "Expected type, collection, let, command, predicate, or flags after 'export'",
                        _filePath, Current.Line);
            }
            else if (Current.Kind == TokenKind.TypeKeyword)
            {
                pendingDocComment = null;
                typeDefinitions.Add(ParseTypeDefinition());
            }
            else if (Current.Kind == TokenKind.CollectionKeyword)
            {
                pendingDocComment = null;
                collectionDeclarations.Add(ParseCollectionDeclaration());
            }
            else if (Current.Kind == TokenKind.ImportKeyword)
            {
                throw new ParseException(
                    "Import statements must appear before type definitions, predicates, and command statements",
                    _filePath, Current.Line);
            }
            else if (Current.Kind == TokenKind.CommandKeyword)
            {
                commands.AddRange(ParseLetCommandChain(pendingDocComment));
                pendingDocComment = null;
            }
            else if (Current.Kind == TokenKind.LetKeyword)
            {
                // Peek ahead: let <name> = <identifier>( → let-command (produces CommandBlock)
                // Otherwise → let-declaration (produces LetDeclaration)
                if (IsLetCommand())
                {
                    commands.Add(ParseLetCommandFromLet(pendingDocComment));
                }
                else
                {
                    letDeclarations.Add(ParseLetDeclaration());
                }
                pendingDocComment = null;
            }
            else if (Current.Kind == TokenKind.ForeachKeyword)
            {
                commands.Add(ParseForeachBlock(pendingDocComment));
                pendingDocComment = null;
            }
            else if (Current.Kind == TokenKind.Identifier && IsActionInvocation())
            {
                commands.Add(ParseActionInvocation(pendingDocComment));
                pendingDocComment = null;
            }
            else if (Current.Kind == TokenKind.RunKeyword)
            {
                runInvocations.Add(ParseRunInvocation());
                pendingDocComment = null;
            }
            else if (Current.Kind == TokenKind.PredicateKeyword)
            {
                pendingDocComment = null;
                predicates.Add(ParsePredicateDefinition());
            }
            else if (Current.Kind == TokenKind.FunctionKeyword)
            {
                pendingDocComment = null;
                functions.Add(ParseFunctionDefinition());
            }
            else if (Current.Kind == TokenKind.FlagsKeyword)
            {
                pendingDocComment = null;
                flagsDefinitions.Add(ParseFlagsDefinition());
            }
            else
            {
                throw new ParseException(
                    $"Unexpected token '{Current.Value}' — expected predicate, let, command, foreach, type, flags, or collection",
                    _filePath, Current.Line);
            }
        }

        return new ScriptFile(_filePath, imports, typeDefinitions, collectionDeclarations, letDeclarations, predicates, functions, commands, runInvocations, feedPaths.Count > 0 ? feedPaths : null, flagsDefinitions.Count > 0 ? flagsDefinitions : null);
    }

    private string CollectDocComment()
    {
        var lines = new List<string>();
        while (Current.Kind == TokenKind.DocComment)
        {
            lines.Add(Advance().Value);
        }
        return string.Join("\n", lines);
    }

    // import <package-name>
    private string ParseImport()
    {
        Expect(TokenKind.ImportKeyword);
        var name = Expect(TokenKind.Identifier);
        return name.Value;
    }

    // type Name = { ... } | type Name = Base & { ... }
    private TypeDefinition ParseTypeDefinition(bool isExported = false)
    {
        int line = Current.Line;
        Expect(TokenKind.TypeKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.Equals);

        string? baseType = null;

        // Check if this is a subtype: Identifier & { ... }
        if (Current.Kind == TokenKind.Identifier)
        {
            baseType = Advance().Value;
            Expect(TokenKind.Ampersand);
        }

        var properties = ParsePropertyBlock();
        return new TypeDefinition(name.Value, baseType, properties, line, isExported);
    }

    // flags Visibility = Public | Protected | Private | Internal
    private FlagsDefinition ParseFlagsDefinition(bool isExported = false)
    {
        int line = Current.Line;
        Expect(TokenKind.FlagsKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.Equals);

        var members = new List<string>();
        members.Add(Expect(TokenKind.Identifier).Value);
        while (Current.Kind == TokenKind.Pipe)
        {
            Advance();
            members.Add(Expect(TokenKind.Identifier).Value);
        }
        return new FlagsDefinition(name.Value, members, line, isExported);
    }

    private List<PropertyDefinition> ParsePropertyBlock()
    {
        Expect(TokenKind.LBrace);
        var properties = new List<PropertyDefinition>();

        while (Current.Kind != TokenKind.RBrace && Current.Kind != TokenKind.Eof)
        {
            properties.Add(ParseProperty());
            // Allow trailing comma
            if (Current.Kind == TokenKind.Comma)
                Advance();
        }

        Expect(TokenKind.RBrace);
        return properties;
    }

    private PropertyDefinition ParseProperty()
    {
        int line = Current.Line;
        var propName = Expect(TokenKind.Identifier);
        Expect(TokenKind.Colon);

        bool isCollection = false;
        bool isOptional = false;

        // Check for [T] list type
        if (Current.Kind == TokenKind.LBracket)
        {
            isCollection = true;
            Advance();
            var typeName = Expect(TokenKind.Identifier);
            Expect(TokenKind.RBracket);

            if (Current.Kind == TokenKind.QuestionMark)
            {
                isOptional = true;
                Advance();
            }

            return new PropertyDefinition(propName.Value, typeName.Value, isOptional, isCollection, line);
        }

        var typeToken = Expect(TokenKind.Identifier);

        if (Current.Kind == TokenKind.QuestionMark)
        {
            isOptional = true;
            Advance();
        }

        return new PropertyDefinition(propName.Value, typeToken.Value, isOptional, isCollection, line);
    }

    // collection Types : [Type]
    private CollectionDeclaration ParseCollectionDeclaration(bool isExported = false)
    {
        int line = Current.Line;
        Expect(TokenKind.CollectionKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.Colon);
        Expect(TokenKind.LBracket);
        var itemType = Expect(TokenKind.Identifier);
        Expect(TokenKind.RBracket);
        return new CollectionDeclaration(name.Value, itemType.Value, line, isExported);
    }

    private PredicateDefinition ParsePredicateDefinition(bool isExported = false)
    {
        int line = Current.Line;
        Expect(TokenKind.PredicateKeyword); // 'predicate' keyword is required
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.LParen);
        // Support both (Type) and ([Type]) syntax
        bool hasBrackets = Current.Kind == TokenKind.LBracket;
        if (hasBrackets) Advance();
        var paramType = Expect(TokenKind.Identifier);
        if (hasBrackets) Expect(TokenKind.RBracket);
        string? constraint = null;
        if (Current.Kind == TokenKind.Colon)
        {
            Advance();
            constraint = Expect(TokenKind.Identifier).Value;
        }
        Expect(TokenKind.RParen);

        // Optional narrowing type annotation: foo(T) : NarrowedType => ...
        string? narrowedType = null;
        if (Current.Kind == TokenKind.Colon && _pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.Identifier)
        {
            Advance(); // consume ':'
            narrowedType = Advance().Value; // consume type name
        }

        Expect(TokenKind.Arrow);
        var body = ParseExpression();
        return new PredicateDefinition(name.Value, paramType.Value, constraint, body, line, isExported, narrowedType);
    }

    // function name(InputType, param1: type1, param2: type2) => ReturnType {
    //     Field1 = expr,
    //     Field2 = expr
    // }
    private FunctionDefinition ParseFunctionDefinition(bool isExported = false)
    {
        int line = Current.Line;
        Expect(TokenKind.FunctionKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.LParen);

        // First parameter is the input type (no name, just type)
        var inputType = Expect(TokenKind.Identifier).Value;

        // Additional named parameters: , name: type
        var parameters = new List<FunctionParameter>();
        while (Current.Kind == TokenKind.Comma)
        {
            Advance(); // consume ','
            var paramName = Expect(TokenKind.Identifier).Value;
            Expect(TokenKind.Colon);
            var paramTypeName = Expect(TokenKind.Identifier).Value;
            parameters.Add(new FunctionParameter(paramName, paramTypeName));
        }

        Expect(TokenKind.RParen);
        Expect(TokenKind.Arrow); // =>
        var returnType = Expect(TokenKind.Identifier).Value;
        Expect(TokenKind.LBrace);

        // Parse field mappings: FieldName = Expression, ...
        var fieldMappings = new Dictionary<string, Expression>();
        while (Current.Kind != TokenKind.RBrace && Current.Kind != TokenKind.Eof)
        {
            var fieldName = Expect(TokenKind.Identifier).Value;
            Expect(TokenKind.Equals);
            var fieldExpr = ParseExpression();
            fieldMappings[fieldName] = fieldExpr;

            if (Current.Kind == TokenKind.Comma)
                Advance();
        }

        Expect(TokenKind.RBrace);
        return new FunctionDefinition(name.Value, inputType, returnType, parameters, fieldMappings, line, isExported);
    }

    private LetDeclaration ParseLetDeclaration(bool isExported = false)
    {
        int line = Current.Line;
        Expect(TokenKind.LetKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.Equals);
        // Handle runtime:: prefix (e.g., let Code = runtime::Codebase)
        bool isRuntime = false;
        if (Current.Kind == TokenKind.Identifier && Current.Value == "runtime" &&
            _pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.DoubleColon)
        {
            Advance(); // consume 'runtime'
            Advance(); // consume '::'
            isRuntime = true;
        }

        // Value binding: let Name = [literal, literal, ...]
        if (Current.Kind == TokenKind.LBracket)
        {
            var listExpr = ParseListLiteral();
            return new LetDeclaration(name.Value, "", [], line, isExported, isRuntime, ValueExpression: listExpr);
        }

        // Value binding: let Name = { Field = expr, ... }
        if (Current.Kind == TokenKind.LBrace)
        {
            var objExpr = ParseObjectLiteral(null);
            return new LetDeclaration(name.Value, "", [], line, isExported, isRuntime, ValueExpression: objExpr);
        }

        // Parse the RHS as an expression (handles colon chains via ParsePostfix)
        var expr = ParseExpression();

        // Handle Load('path') and Parse('file', [Type]) as value bindings
        if (expr is FunctionCallExpr call && call.Name is "Load" or "Parse")
        {
            return new LetDeclaration(name.Value, "", [], line, isExported, isRuntime, ValueExpression: call);
        }

        var (baseCollection, filters, exclusions) = DecomposeCollectionExpression(expr);
        return new LetDeclaration(name.Value, baseCollection, filters, line, isExported, isRuntime, Exclusions: exclusions);
    }

    // Check if the current token is an identifier followed by '(' — action invocation
    private bool IsActionInvocation()
    {
        return _pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.LParen;
    }

    // Peek ahead to check if this is a let-command: let <name> = <identifier>(...) or let <name> = foreach ...
    private bool IsLetCommand()
    {
        int i = _pos;
        if (i >= _tokens.Count || _tokens[i].Kind != TokenKind.LetKeyword) return false;
        i++;
        if (i >= _tokens.Count || _tokens[i].Kind != TokenKind.Identifier) return false;
        i++;
        if (i >= _tokens.Count || _tokens[i].Kind != TokenKind.Equals) return false;
        i++;
        // foreach after = means this is a let-command
        if (i < _tokens.Count && _tokens[i].Kind == TokenKind.ForeachKeyword) return true;
        // Check for identifier followed by ( — this is an action invocation
        // But Load() is a value binding, not a command action
        if (i >= _tokens.Count || _tokens[i].Kind != TokenKind.Identifier) return false;
        if (_tokens[i].Value is "Load" or "Parse") return false;
        i++;
        return i < _tokens.Count && _tokens[i].Kind == TokenKind.LParen;
    }

    private CommandBlock ParseLetCommandFromLet(string? docComment)
    {
        int line = Current.Line;
        Expect(TokenKind.LetKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.Equals);
        CommandBlock block;
        if (Current.Kind == TokenKind.ForeachKeyword)
            block = ParseForeachBlock(docComment);
        else
            block = ParseActionInvocation(docComment);
        return block with { Name = name.Value, IsCommand = true };
    }

    // Parse: command <name>[(<params>)] = <action>(...) | <command-ref>
    // Each element can optionally have :guard after it
    private CommandBlock ParseLetCommand(string? docComment, bool isExported = false)
    {
        int line = Current.Line;
        Expect(TokenKind.CommandKeyword);
        var name = Expect(TokenKind.Identifier);

        // Optional parameter list: command CHECK(violations) = ...
        List<string>? parameters = null;
        if (Current.Kind == TokenKind.LParen)
        {
            Advance(); // consume '('
            parameters = [];
            while (Current.Kind != TokenKind.RParen && Current.Kind != TokenKind.Eof)
            {
                parameters.Add(Expect(TokenKind.Identifier).Value);
                if (Current.Kind == TokenKind.Comma)
                    Advance();
            }
            Expect(TokenKind.RParen);
        }

        Expect(TokenKind.Equals);
        var block = ParseCommandAtom(name.Value, docComment, line);
        return block with { Parameters = parameters, IsExported = isExported };
    }

    // Parse a single command atom: foreach..., <identifier>(...), or a command reference
    private CommandBlock ParseCommandAtom(string commandName, string? docComment, int line)
    {
        CommandBlock block;
        if (Current.Kind == TokenKind.ForeachKeyword)
        {
            block = ParseForeachBlock(docComment);
            block = block with { Name = commandName, IsCommand = true };
        }
        else if (Current.Kind == TokenKind.Identifier && IsActionInvocation())
        {
            block = ParseActionInvocation(docComment);
            block = block with { Name = commandName, IsCommand = true };
        }
        else
        {
            // Command reference: identifier
            var refName = Expect(TokenKind.Identifier).Value;
            block = new CommandBlock(commandName, "",
                null, [], line, docComment, IsCommand: true, CommandRef: refName);
        }

        // Check for :guard on this element
        if (Current.Kind == TokenKind.Colon)
        {
            Advance(); // consume ':'
            var guard = ParseExpression();
            block = block with { Guard = guard };
        }

        return block;
    }

    // Parse additional &-chained elements after ParseLetCommand
    private List<CommandBlock> ParseLetCommandChain(string? docComment, bool isExported = false)
    {
        var first = ParseLetCommand(docComment, isExported);
        var commands = new List<CommandBlock> { first };

        while (Current.Kind == TokenKind.Ampersand)
        {
            Advance(); // consume '&'
            var stmt = ParseCommandAtom(first.Name, null, Current.Line);
            commands.Add(stmt);
        }

        return commands;
    }

    // Parse: foreach <collection-expr> => <action-invocation>
    private CommandBlock ParseForeachBlock(string? docComment)
    {
        int line = Current.Line;
        Expect(TokenKind.ForeachKeyword);

        // Parse the collection expression
        var expr = ParseExpression();
        var (collection, filters, exclusions) = DecomposeCollectionExpression(expr);

        Expect(TokenKind.Arrow); // =>

        // Parse the action invocation (string-args only)
        var block = ParseActionInvocation(docComment);

        // Merge collection info from foreach into the block
        string name = DeriveRuleId(collection, filters);
        return block with
        {
            Name = name,
            Collection = collection,
            Filters = filters,
            Line = line,
            Exclusions = exclusions
        };
    }

    // Generic action invocation: <identifier>(<args>)
    // Arguments are comma-separated string literals and/or collection expressions.
    // One string arg → message template. Two string args → first is output path, second is message.
    private CommandBlock ParseActionInvocation(string? docComment)
    {
        int line = Current.Line;
        string actionName = Expect(TokenKind.Identifier).Value;
        Expect(TokenKind.LParen);

        string? collection = null;
        var filters = new List<Expression>();
        var stringArgs = new List<string>();
        Expression? exclusions = null;

        // Parse comma-separated arguments (strings and collection expressions)
        while (Current.Kind != TokenKind.RParen && Current.Kind != TokenKind.Eof)
        {
            if (Current.Kind == TokenKind.StringLiteral)
            {
                stringArgs.Add(Advance().Value);
            }
            else
            {
                var expr = ParseExpression();
                (collection, filters, exclusions) = DecomposeCollectionExpression(expr);
            }

            if (Current.Kind == TokenKind.Comma)
                Advance();
        }

        Expect(TokenKind.RParen);

        // Assign string args: 1 string = message, 2+ strings = path + message
        string messageTemplate = "";
        string? outputPath = null;

        if (stringArgs.Count == 1)
        {
            messageTemplate = stringArgs[0];
        }
        else if (stringArgs.Count >= 2)
        {
            outputPath = stringArgs[0];
            messageTemplate = stringArgs[1];
        }

        string name = collection is not null
            ? DeriveRuleId(collection, filters)
            : $"action_{line}";

        return new CommandBlock(name, messageTemplate,
            collection, filters, line, docComment, ActionName: actionName, OutputPath: outputPath, Exclusions: exclusions);
    }

    // Parse: RUN <commandName>(<arg1>, <arg2>, ...)
    private RunInvocation ParseRunInvocation()
    {
        int line = Current.Line;
        Expect(TokenKind.RunKeyword);
        var commandName = Expect(TokenKind.Identifier).Value;
        Expect(TokenKind.LParen);
        var args = new List<Expression>();
        while (Current.Kind != TokenKind.RParen && Current.Kind != TokenKind.Eof)
        {
            args.Add(ParseExpression());
            if (Current.Kind == TokenKind.Comma)
                Advance();
        }
        Expect(TokenKind.RParen);
        return new RunInvocation(commandName, args, line);
    }

    private static string DeriveRuleId(string collection, List<Expression> filters)
    {
        if (filters.Count == 0) return collection;
        var first = filters[0];
        string filterName = first switch
        {
            IdentifierExpr id => id.Name,
            FunctionCallExpr fc => fc.Name,
            UnaryExpr { Operand: IdentifierExpr id } => id.Name,
            UnaryExpr { Operand: FunctionCallExpr fc } => fc.Name,
            _ => ""
        };
        return string.IsNullOrEmpty(filterName) ? collection : $"{collection}.{filterName}";
    }

    /// Decompose an expression tree (from ParseExpression) into the structured fields
    /// that LetDeclaration and CommandBlock need: baseCollection, filters, and exclusions.
    /// Input: Types:csharp:isClient:!isClientOptions
    /// → (baseCollection: "Types", filters: [csharp, isClient, !isClientOptions], exclusions: null)
    /// Input: Types:csharp:isClient:toError("msg") - Accepted
    /// → (baseCollection: "Types", filters: [csharp, isClient, toError("msg")], exclusions: Accepted)
    public static (string baseCollection, List<Expression> filters, Expression? exclusions) DecomposeCollectionExpression(Expression expr)
    {
        // Handle set subtraction: CollectionChain - ExclusionExpr
        Expression? exclusions = null;
        if (expr is BinaryExpr bin && bin.Operator == "-")
        {
            exclusions = bin.Right;
            expr = bin.Left;
        }

        var filters = new List<Expression>();

        // Collect PredicateCallExprs from outermost to innermost
        var calls = new List<PredicateCallExpr>();
        var current = expr;
        while (current is PredicateCallExpr pc)
        {
            calls.Add(pc);
            current = pc.Target;
        }

        if (current is not IdentifierExpr id)
        {
            // Support dotted access: Object.Property as collection base (e.g., Code.Statements)
            if (current is MemberAccessExpr ma && ma.Target is IdentifierExpr parentId)
            {
                var dottedBase = $"{parentId.Name}.{ma.Member}";

                calls.Reverse();
                foreach (var call in calls)
                {
                    Expression filter;
                    if (call.Args.Count > 0)
                        filter = new FunctionCallExpr(call.Name, call.Args);
                    else
                        filter = new IdentifierExpr(call.Name);
                    if (call.Negated)
                        filter = new UnaryExpr("!", filter);
                    filters.Add(filter);
                }
                return (dottedBase, filters, exclusions);
            }

            var exprText = current switch
            {
                UnaryExpr u => $"'!{(u.Operand is FunctionCallExpr fc ? fc.Name : u.Operand.GetType().Name)}(...)'",
                FunctionCallExpr f => $"'{f.Name}(...)'",
                _ => current.GetType().Name
            };
            throw new InvalidOperationException(
                $"'let' declaration must start with a collection name (e.g., Statements:filter), but got {exprText} which is a filter expression, not a collection.");
        }

        // Process from innermost to outermost
        calls.Reverse();
        foreach (var call in calls)
        {
            Expression filter;
            if (call.Args.Count > 0)
                filter = new FunctionCallExpr(call.Name, call.Args);
            else
                filter = new IdentifierExpr(call.Name);
            if (call.Negated)
                filter = new UnaryExpr("!", filter);
            filters.Add(filter);
        }

        return (id.Name, filters, exclusions);
    }

    // Expression parsing with operator precedence
    private Expression ParseExpression() => ParseTernary();

    private Expression ParseTernary()
    {
        var expr = ParseOr();
        if (Current.Kind != TokenKind.QuestionMark) return expr;
        Advance(); // consume ?
        var savedSkipPipe = _skipPipe;
        _skipPipe = true;
        var trueExpr = ParseTernary(); // recursive for nesting in true branch
        _skipPipe = savedSkipPipe;
        Expect(TokenKind.Pipe); // consume |
        var falseExpr = ParseTernary(); // right-associative
        return new ConditionalExpr(expr, trueExpr, falseExpr);
    }

    private Expression ParseOr()
    {
        var left = ParseAnd();
        while (Current.Kind == TokenKind.OrOr)
        {
            Advance();
            left = new BinaryExpr(left, "||", ParseAnd());
        }
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseEquality();
        while (Current.Kind == TokenKind.AndAnd)
        {
            Advance();
            left = new BinaryExpr(left, "&&", ParseEquality());
        }
        return left;
    }

    private Expression ParseEquality()
    {
        var left = ParseBitwiseOr();
        if (Current.Kind is TokenKind.EqualEqual or TokenKind.NotEqual
            or TokenKind.GreaterThan or TokenKind.LessThan
            or TokenKind.GreaterEqual or TokenKind.LessEqual)
        {
            var op = Advance();
            left = new BinaryExpr(left, op.Value, ParseBitwiseOr());
        }
        return left;
    }

    private Expression ParseBitwiseOr()
    {
        var left = ParseBitwiseAnd();
        while (!_skipPipe && Current.Kind == TokenKind.Pipe)
        {
            Advance();
            left = new BinaryExpr(left, "|", ParseBitwiseAnd());
        }
        return left;
    }

    private Expression ParseBitwiseAnd()
    {
        var left = ParseAdditive();
        while (Current.Kind == TokenKind.Ampersand)
        {
            Advance();
            left = new BinaryExpr(left, "&", ParseAdditive());
        }
        return left;
    }

    private Expression ParseAdditive()
    {
        var left = ParseUnary();
        while (Current.Kind is TokenKind.Minus or TokenKind.Plus)
        {
            var op = Advance();
            left = new BinaryExpr(left, op.Value, ParseUnary());
        }
        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.Kind == TokenKind.Not)
        {
            Advance();
            return new UnaryExpr("!", ParseUnary());
        }
        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (Current.Kind == TokenKind.Dot)
            {
                Advance();
                var member = Expect(TokenKind.Identifier);
                if (Current.Kind == TokenKind.LParen)
                {
                    // .Transform(args) — PascalCase transforms use dot syntax
                    Advance();
                    var args = ParseArgList();
                    Expect(TokenKind.RParen);
                    expr = new PredicateCallExpr(expr, member.Value, args, false);
                }
                else
                {
                    expr = new MemberAccessExpr(expr, member.Value);
                }
            }
            else if (Current.Kind == TokenKind.Colon && _pos + 1 < _tokens.Count
                && (_tokens[_pos + 1].Kind == TokenKind.Identifier
                    || _tokens[_pos + 1].Kind == TokenKind.Not))
            {
                Advance(); // consume ':'
                bool negated = false;
                if (Current.Kind == TokenKind.Not)
                {
                    negated = true;
                    Advance();
                }
                var predName = Expect(TokenKind.Identifier);
                var normalizedName = NormalizePredicateName(predName.Value);
                if (Current.Kind == TokenKind.LParen)
                {
                    Advance();
                    var args = ParseArgList();
                    Expect(TokenKind.RParen);
                    expr = new PredicateCallExpr(expr, normalizedName, args, negated);
                }
                else
                {
                    expr = new PredicateCallExpr(expr, normalizedName, [], negated);
                }
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private Expression ParsePrimary()
    {
        switch (Current.Kind)
        {
            case TokenKind.Identifier:
            {
                var token = Advance();
                // Direct function call: Name(args)
                if (Current.Kind == TokenKind.LParen)
                {
                    Advance();
                    var args = ParseArgList();
                    Expect(TokenKind.RParen);
                    return new FunctionCallExpr(token.Value, args);
                }
                // Record construction: TypeName { Field: expr, ... }
                if (Current.Kind == TokenKind.LBrace)
                {
                    return ParseObjectLiteral(token.Value);
                }
                return new IdentifierExpr(token.Value);
            }
            case TokenKind.True:
                Advance();
                return new LiteralExpr(true);
            case TokenKind.False:
                Advance();
                return new LiteralExpr(false);
            case TokenKind.StringLiteral:
            {
                var token = Advance();
                return new LiteralExpr(token.Value);
            }
            case TokenKind.IntLiteral:
            {
                var token = Advance();
                return new LiteralExpr(int.Parse(token.Value));
            }
            case TokenKind.NumberLiteral:
            {
                var token = Advance();
                return new LiteralExpr(double.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            case TokenKind.LParen:
            {
                Advance();
                var savedPipe = _skipPipe;
                _skipPipe = false;
                var expr = ParseExpression();
                _skipPipe = savedPipe;
                Expect(TokenKind.RParen);
                return expr;
            }
            case TokenKind.LBracket:
            {
                return ParseListLiteral();
            }
            case TokenKind.LBrace:
            {
                return ParseObjectLiteral(null);
            }
            default:
                throw new ParseException(
                    $"Unexpected token {Current.Kind} '{Current.Value}'",
                    _filePath, Current.Line);
        }
    }

    private List<Expression> ParseArgList()
    {
        var args = new List<Expression>();
        if (Current.Kind != TokenKind.RParen)
        {
            args.Add(ParseExpression());
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                args.Add(ParseExpression());
            }
        }
        return args;
    }

    // [expr, expr, ...] or []
    private ListLiteralExpr ParseListLiteral()
    {
        Advance(); // consume [
        var elements = new List<Expression>();
        if (Current.Kind != TokenKind.RBracket)
        {
            elements.Add(ParseExpression());
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                elements.Add(ParseExpression());
            }
        }
        Expect(TokenKind.RBracket);
        return new ListLiteralExpr(elements);
    }

    // TypeName { Field: expr, Field2: expr2, ... } or { Field = expr, ... }
    private ObjectLiteralExpr ParseObjectLiteral(string? typeName)
    {
        Advance(); // consume {
        var fields = new Dictionary<string, Expression>();
        while (Current.Kind != TokenKind.RBrace && Current.Kind != TokenKind.Eof)
        {
            // Accept identifier, true/false as field names
            var fieldToken = Current;
            var fieldName = Current.Kind switch
            {
                TokenKind.Identifier or TokenKind.True or TokenKind.False => Advance().Value,
                _ => Expect(TokenKind.Identifier).Value
            };

            if (fields.ContainsKey(fieldName))
                throw new ParseException($"Duplicate field '{fieldName}' in object literal", _filePath, fieldToken.Line);

            // Accept both ':' and '=' for field assignment
            if (Current.Kind == TokenKind.Colon)
                Advance();
            else
                Expect(TokenKind.Equals);

            var fieldExpr = ParseExpression();
            fields[fieldName] = fieldExpr;

            if (Current.Kind == TokenKind.Comma)
                Advance();
        }
        Expect(TokenKind.RBrace);
        return new ObjectLiteralExpr(typeName, fields);
    }

    /// <summary>Expands legacy 2-letter predicate abbreviations to their full camelCase names.</summary>
    private static string NormalizePredicateName(string name) => name switch
    {
        "eq" => "equals",
        "ne" => "notEquals",
        "sw" => "startsWith",
        "ew" => "endsWith",
        "ct" => "contains",
        "ca" => "containsAny",
        "rx" => "matches",
        "sm" => "sameAs",
        "gt" => "greaterThan",
        "lt" => "lessThan",
        "ge" => "greaterOrEqual",
        "le" => "lessOrEqual",
        _ => name
    };
}
