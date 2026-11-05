// Projects the Cop parser's syntax and semantic facts into the shared code model.

using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Maps the Cop AST to the shared code source model.
/// </summary>
public class CopSourceParser : ISourceParser
{
    static CopSourceParser() => CopFunctionDeclaration.RegisterCacheFactory();

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => [".cop"];
    /// <inheritdoc />
    public override string Language => "cop";

    /// <inheritdoc />
    public override SourceFile? Parse(string filePath, string sourceText)
    {
        if (IsNegativeParserFixture(filePath, sourceText))
        {
            return null;
        }

        ModuleNode module;
        try
        {
            module = Cop.Lang.Parser.CopParser.Parse(sourceText, filePath);
        }
        catch (ParseException ex)
        {
            return new SourceFile(filePath, "cop", [], [], sourceText)
            {
                ParseErrors = [$"{filePath}({ex.Line}): error: {ex.Message}"],
                CommentLines = ExpressionCollector.CollectCommentLines(sourceText)
            };
        }

        return ParseModule(filePath, sourceText, module);
    }

    internal static bool IsNegativeParserFixture(string filePath, string sourceText)
    {
        using StringReader reader = new StringReader(sourceText);
        while (reader.ReadLine() is { } line)
        {
            string header = line.Trim();
            if (header.Length == 0)
            {
                continue;
            }

            if (!header.StartsWith('#'))
            {
                break;
            }

            if (header != "# @expect parse-error")
            {
                continue;
            }

            if (Cop.Core.CopDiagnostics.Summaries)
            {
                Console.Error.WriteLine($"[diag] Excluded '{filePath}': explicitly marked '# @expect parse-error' parser test input.");
            }

            return true;
        }
        return false;
    }

    internal SourceFile ParseModule(string filePath, string sourceText, ModuleNode module,
        Dictionary<StatementInfo, Cop.Lang.Ast.Expression>? expressions = null)
    {
        List<TypeDeclaration> types = new List<TypeDeclaration>();
        List<StatementInfo> statements = new List<StatementInfo>();
        Dictionary<FunctionDecl, CopFunctionDeclaration> functions = new Dictionary<FunctionDecl, CopFunctionDeclaration>();
        List<string> usings = module.Declarations.OfType<ImportDecl>().Select(i => i.ModuleName).ToList();

        // Map type definitions → TypeDeclaration
        foreach (TypeDecl td in module.Declarations.OfType<TypeDecl>())
        {
            Modifier modifiers = td.IsExported ? Modifier.Public : Modifier.None;
            List<FieldDeclaration> fields = td.Properties.Select(p =>
                new FieldDeclaration(p.Name, MakeTypeRef(p.Type.Name, p.Type.IsCollection), Modifier.Public, p.Line))
                .ToList();
            List<string> baseTypes = td.BaseType is not null ? new List<string> { td.BaseType } : [];

            types.Add(new TypeDeclaration(td.Name, TypeKind.Class, modifiers,
                baseTypes, [], [], [], [], [], td.Line)
            {
                Fields = fields,
                HasDocComment = td.DocComment is not null,
                DocComment = td.DocComment
            });
        }

        // Map flags definitions → TypeDeclaration (Enum)
        foreach (FlagsDecl fd in module.Declarations.OfType<FlagsDecl>())
        {
            Modifier modifiers = fd.IsExported ? Modifier.Public : Modifier.None;
            types.Add(new TypeDeclaration(fd.Name, TypeKind.Enum, modifiers,
                [], [], [], [], [], fd.Members, fd.Line));
        }

        // Map enum definitions → TypeDeclaration (Enum)
        foreach (EnumDecl ed in module.Declarations.OfType<EnumDecl>())
        {
            Modifier modifiers = ed.IsExported ? Modifier.Public : Modifier.None;
            types.Add(new TypeDeclaration(ed.Name, TypeKind.Enum, modifiers,
                [], [], [], [], [], ed.Members, ed.Line));
        }

        foreach (FunctionDecl fn in module.Declarations.OfType<FunctionDecl>())
        {
            MethodDeclaration method = new MethodDeclaration(fn.Name, fn.IsExported ? Modifier.Public : Modifier.None,
                [], fn.ReturnType is { } returnType ? MakeTypeRef(returnType.Name, returnType.IsCollection)
                    : fn.IsPredicate ? MakeTypeRef("bool") : null,
                fn.Params.Select(p => new ParameterDeclaration(p.Name,
                    p.Type is { } type ? MakeTypeRef(type.Name, type.IsCollection) : null,
                    false, false, false, p.Line)).ToList(), fn.Line)
            {
                HasDocComment = fn.DocComment is not null,
                DocComment = fn.DocComment
            };
            functions.Add(fn, new CopFunctionDeclaration(method,
                fn.Body is IntrinsicBody, fn.IsPredicate, fn.IsCommand, fn.Guard is not null));

            List<string> keywords = new List<string> { fn.IsPredicate ? "predicate" : "function" };
            if (fn.Body is IntrinsicBody)
            {
                keywords.Add("intrinsic");
            }

            statements.Add(new StatementInfo(fn.IsCommand ? "command" : "declaration", keywords,
                fn.IsPredicate ? fn.Params.FirstOrDefault()?.Type?.Name : fn.ReturnType?.Name,
                fn.Name, [], fn.Line, false));
        }

        // Map import statements
        foreach (ImportDecl imp in module.Declarations.OfType<ImportDecl>())
        {
            statements.Add(new StatementInfo("import", [], null, imp.ModuleName, [], imp.Line, false));
        }

        // Map let declarations
        foreach (LetDecl let in module.Declarations.OfType<LetDecl>())
        {
            List<string> keywords = new List<string> { "let" };
            if (let.IsExported)
            {
                keywords.Add("export");
            }

            StatementInfo statement = new StatementInfo("declaration", keywords,
                null, let.Name, [], let.Line, false) { ValueExpression = ConvertExpression(let.Value) };
            statements.Add(statement);
            expressions?.Add(statement, let.Value);
        }

        // Map commands → statements
        foreach (CommandDecl cmd in module.Declarations.OfType<CommandDecl>())
        {
            statements.Add(new StatementInfo("command", [],
                null, cmd.Name, [], cmd.Line, false));
        }

        new ExpressionCollector(statements, functions, expressions).Visit(module);

        return new SourceFile(filePath, "cop", types, statements, sourceText)
        {
            Usings = usings,
            Imports = module.Declarations.OfType<ImportDecl>().Select(i => new ImportInfo(i.ModuleName, i.Line)).ToList(),
            Methods = functions.Values.Cast<MethodDeclaration>().ToList(),
            CommentLines = ExpressionCollector.CollectCommentLines(sourceText)
        };
    }

    private sealed class ExpressionCollector(List<StatementInfo> statements,
        IReadOnlyDictionary<FunctionDecl, CopFunctionDeclaration> functions,
        Dictionary<StatementInfo, Cop.Lang.Ast.Expression>? expressions) : AstWalker
    {
        private CopFunctionDeclaration? _function;
        private StatementInfo? _parent;

        private void Add(StatementInfo statement)
        {
            statement.Method = _function;
            statement.Parent = _parent;
            statements.Add(statement);
            if (_parent is not null)
            {
                _parent._children.Add(statement);
            }
            else
            {
                _function?.Statements.Add(statement);
            }
        }

        private object? Within(StatementInfo statement, Func<object?> visit)
        {
            Add(statement);
            StatementInfo? previous = _parent;
            _parent = statement;
            try { return visit(); }
            finally { _parent = previous; }
        }

        /// <inheritdoc />
        public override object? VisitCallExpr(Cop.Lang.Ast.CallExpr node)
        {
            ExpressionInfo target = ConvertExpression(node.Callee);
            StatementInfo statement = new StatementInfo("call", [],
                target.Target?.Name, target.Name, [], node.Line, _function is not null)
            {
                CallTarget = target,
                ArgumentExpressions = node.Args.Select(ConvertExpression).ToList(),
                Method = _function
            };
            expressions?.Add(statement, node);
            return Within(statement, () => base.VisitCallExpr(node));
        }

        /// <inheritdoc />
        public override object? VisitLetStatement(LetStatement node)
        {
            StatementInfo statement = new StatementInfo("declaration", ["let"], node.TypeAnnotation?.Name, node.Name, [],
                node.Line, _function is not null) { ValueExpression = ConvertExpression(node.Value) };
            expressions?.Add(statement, node.Value);
            return Within(statement, () => base.VisitLetStatement(node));
        }

        /// <inheritdoc />
        public override object? VisitForEachStatement(ForEachStatement node) =>
            Within(new StatementInfo("foreach", node.IsAsync ? ["async"] : [], null, node.Variable, [],
                node.Line, _function is not null), () => base.VisitForEachStatement(node));

        /// <inheritdoc />
        public override object? VisitPipelineStatement(PipelineStatement node) =>
            Within(new StatementInfo("pipeline", [], null, null, [], node.Line, _function is not null),
                () => base.VisitPipelineStatement(node));

        /// <inheritdoc />
        public override object? VisitLetDecl(LetDecl node)
        {
            StatementInfo declaration = statements.Single(s => s.Kind == "declaration" && s.MemberName == node.Name && s.Line == node.Line);
            StatementInfo? previous = _parent;
            _parent = declaration;
            base.VisitLetDecl(node);
            _parent = previous;
            return null;
        }

        /// <summary>
        /// Finds comment line numbers while respecting Cop string literals.
        /// </summary>
        public static HashSet<int> CollectCommentLines(string text)
        {
            HashSet<int> result = new HashSet<int>();
            int line = 1;
            bool quoted = false, verbatim = false, triple = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    line++;
                }

                if (quoted)
                {
                    if (!verbatim && !triple && c == '\\' && i + 1 < text.Length)
                    {
                        if (text[++i] == '\n')
                        {
                            line++;
                        }
                    }
                    else if (c == '\'')
                    {
                        if (triple)
                        {
                            if (i + 2 < text.Length && text[i + 1] == '\'' && text[i + 2] == '\'')
                            { quoted = false; i += 2; }
                        }
                        else if (verbatim && i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    continue;
                }
                if (c == '#')
                {
                    result.Add(line);
                    while (i + 1 < text.Length && text[i + 1] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '\'')
                {
                    quoted = true;
                    verbatim = i > 0 && text[i - 1] == '@';
                    triple = !verbatim && i + 2 < text.Length && text[i + 1] == '\'' && text[i + 2] == '\'';
                    if (triple)
                    {
                        i += 2;
                    }
                }
            }
            return result;
        }

        /// <inheritdoc />
        public override object? VisitFunctionDecl(FunctionDecl node)
        {
            CopFunctionDeclaration? previous = _function;
            _function = functions[node];
            if (node.Body is BlockBody block)
            {
                foreach (Statement statement in block.Statements)
                {
                    Visit(statement);
                }
            }

            base.VisitFunctionDecl(node);
            _function = previous;
            return null;
        }

        protected override object? DefaultVisit(AstNode node)
        {
            if (node is PropertyDecl { ComputedExpr: not null } property)
            {
                Visit(property.ComputedExpr);
            }
            else if (node is ForEachExpr loop)
            {
                Visit(loop.Loop);
            }

            return null;
        }
    }

    private static ExpressionInfo ConvertExpression(Cop.Lang.Ast.Expression expression) => expression switch
    {
        Cop.Lang.Ast.IdentifierExpr id => new("identifier") { Name = id.Name },
        Cop.Lang.Ast.LiteralExpr { Value: string value } => new("stringLiteral") { StringValue = value },
        MemberExpr member => new("memberAccess") { Name = member.Member, Target = ConvertExpression(member.Object) },
        Cop.Lang.Ast.CallExpr call => new("invocation")
        {
            Target = ConvertExpression(call.Callee), Children = call.Args.Select(ConvertExpression).ToList()
        },
        Cop.Lang.Ast.IndexExpr index => new("index")
        {
            Target = ConvertExpression(index.Object), Children = [ConvertExpression(index.Index)]
        },
        Cop.Lang.Ast.BinaryExpr binary => new("binary")
        {
            Name = binary.Op.ToString(), Children = [ConvertExpression(binary.Left), ConvertExpression(binary.Right)]
        },
        Cop.Lang.Ast.UnaryExpr unary => new("unary")
        {
            Name = unary.Op.ToString(), Children = [ConvertExpression(unary.Operand)]
        },
        Cop.Lang.Ast.ConditionalExpr conditional => new("conditional")
        {
            Children = [ConvertExpression(conditional.Condition), ConvertExpression(conditional.Then), ConvertExpression(conditional.Else)]
        },
        Cop.Lang.Ast.ListExpr list => new("list") { Children = list.Elements.Select(ConvertExpression).ToList() },
        _ => new("other")
    };

    private static TypeReference MakeTypeRef(string typeName, bool isCollection = false)
    {
        if (isCollection)
        {
            TypeReference inner = new TypeReference(typeName, null, [], typeName);
            return new TypeReference("List", null, [inner], $"[{typeName}]");
        }
        return new TypeReference(typeName, null, [], typeName);
    }
}
