// Captures bound Cop declarations and expressions for source-model projection.

using Cop.Core;
using ParseException = Cop.Lang.ParseException;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>Execution-only semantic context. File-only caches continue to contain syntax alone.</summary>
internal sealed class CopSemanticSnapshot(string root)
{
    private readonly HashSet<SourceFile> _attached = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Lazy<StaticSourceProgram>> _programs = new(StringComparer.Ordinal);

    internal void Attach(object item)
    {
        SourceFile? file = item switch
        {
            SourceFile source => source,
            StatementInfo statement => statement.File,
            MethodDeclaration method => method.File,
            TypeDeclaration type => type.File,
            _ => null
        };
        if (file is null || file.Language != "cop" || !_attached.Add(file))
        {
            return;
        }

        Lazy<bool> analysis = new Lazy<bool>(() => { Analyze(file); return true; });
        foreach (StatementInfo statement in file.Statements)
        {
            statement.DeferSemanticAnalysis(analysis);
        }
    }

    private StaticSourceProgram ProgramFor(SourceFile file)
    {
        string directory = Path.GetDirectoryName(file.FullPath)!;
        if (!_programs.TryGetValue(directory, out Lazy<StaticSourceProgram>? program))
        {
            program = new(() =>
            {
                List<StaticSourceFile> files = new List<StaticSourceFile>();
                foreach (string? path in Directory.GetFiles(directory, "*.cop").Order(StringComparer.Ordinal))
                {
                    SourceFile? known = _attached.FirstOrDefault(source => source.FullPath == path);
                    string text = known?.RawText ?? File.ReadAllText(path);
                    ExecutionStatistics.RecordInputFile(path);
                    if (CopSourceParser.IsNegativeParserFixture(path, text))
                    {
                        continue;
                    }

                    try { files.Add(new(CopParser.Parse(text, path), path, text)); }
                    catch (ParseException ex) { ProviderErrors.Report($"{path}({ex.Line}): {ex.Message}"); }
                }
                List<string> feeds = new List<string>();
                for (DirectoryInfo? ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                {
                    string packages = Path.Combine(ancestor.FullName, "packages");
                    if (Directory.Exists(packages))
                    {
                        feeds.Add(packages);
                    }
                }
                StaticSourceProgram result = new StaticSourceProgram(files, feeds);
                foreach (string diagnostic in result.Diagnostics)
                {
                    ProviderErrors.Report($"{file.Path}: Cop semantic context: {diagnostic}");
                }

                return result;
            });
            _programs.Add(directory, program);
        }
        return program.Value;
    }

    private void Analyze(SourceFile file)
    {
        try
        {
            ModuleNode module = CopParser.Parse(file.RawText, file.FullPath);
            Dictionary<StatementInfo, Expression> expressions = new Dictionary<StatementInfo, Cop.Lang.Ast.Expression>(ReferenceEqualityComparer.Instance);
            SourceFile syntax = new CopSourceParser().ParseModule(file.FullPath, file.RawText, module, expressions);
            if (syntax.Statements.Count != file.Statements.Count)
            {
                throw new InvalidDataException("Cached Cop statement identities do not match the current syntax.");
            }

            Dictionary<Expression, List<StatementInfo>> sites = new Dictionary<Cop.Lang.Ast.Expression, List<StatementInfo>>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < syntax.Statements.Count; i++)
            {
                if (expressions.TryGetValue(syntax.Statements[i], out Expression? expression))
                {
                    if (!sites.TryGetValue(expression, out List<StatementInfo>? statements))
                    {
                        sites.Add(expression, statements = []);
                    }

                    statements.Add(file.Statements[i]);
                }
            }

            new Projection(ProgramFor(file), sites, root, file.Path).Visit(module);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ParseException or InvalidDataException)
        {
            ProviderErrors.Report($"{file.Path}: Cop semantic analysis failed: {ex.Message}");
        }
    }

    private sealed class Projection(StaticSourceProgram program,
        Dictionary<Cop.Lang.Ast.Expression, List<StatementInfo>> sites, string root, string sourcePath) : AstWalker
    {
        private Dictionary<string, TypeInfo?> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<CallExpr, Cop.Lang.Ast.Expression> _receivers = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<CallExpr> _unsupportedApplications = new(ReferenceEqualityComparer.Instance);

        private static TypeReference? Reference(TypeInfo? type) => type is not { } known ? null
            : known.IsCollection
                ? new("List", null, [new(known.Name, null, [], known.Name)], known.Display)
                : new(known.Name, null, [], known.Display);

        private void Value(Cop.Lang.Ast.Expression expression)
        {
            if (!sites.TryGetValue(expression, out List<StatementInfo>? statements))
            {
                return;
            }

            TypeInfo? type = program.Model.InferType(expression, _locals);
            foreach (StatementInfo? statement in statements.Where(s => s.Kind != "call"))
            {
                statement.ValueType = Reference(type);
            }
        }

        /// <inheritdoc />
        public override object? VisitCallExpr(CallExpr node)
        {
            if (sites.TryGetValue(node, out List<StatementInfo>? statements))
            {
                CallExpr target = node;
                TypeInfo? receiver = null;
                if (_receivers.TryGetValue(node, out Expression? subject))
                {
                    receiver = program.Model.InferType(subject, _locals);
                    target = node with { Args = [subject, .. node.Args] };
                }
                StaticCallInfo selected = _unsupportedApplications.Contains(node)
                    ? new StaticCallInfo(null, [], null, ["Collection/filter application requires runtime dispatch."])
                    : program.Model.ResolveCall(target, _locals);
                StaticDeclarationLocation? location = selected.Declaration is { } function ? program.LocationOf(function) : null;
                CallableReference? callable = null;
                if (selected.Declaration is { } declaration && selected.ParameterTypes.All(type => type is not null))
                {
                    string name = location?.Namespace is { } ns && declaration.Name.StartsWith(ns + ".", StringComparison.Ordinal)
                        ? declaration.Name[(ns.Length + 1)..] : declaration.Name;
                    callable = new(name,
                        $"{declaration.Name}({string.Join(", ", selected.ParameterTypes.Select(type => type!.Value.Display))}) : {selected.ResultType?.Display ?? "?"}")
                    {
                        Parameters = selected.ParameterTypes.Select(type => Reference(type)!).ToList(),
                        ReturnType = Reference(selected.ResultType),
                        Namespace = location?.Namespace,
                        SourceFile = location is null ? null : Path.GetRelativePath(root, location.Path).Replace('\\', '/'),
                        Line = location?.Line
                    };
                }
                foreach (StatementInfo? statement in statements.Where(s => s.Kind == "call"))
                {
                    statement.ReceiverType = Reference(receiver);
                    statement.ValueType = Reference(selected.ResultType);
                    statement.ReferencedCallable = callable;
                }
                foreach (string diagnostic in selected.Diagnostics)
                {
                    ProviderWarnings.ReportOnce("cop", $"{sourcePath}:{node.Line}:{StatementName(node)}",
                        $"{sourcePath}({node.Line}): {diagnostic}");
                }
            }
            return base.VisitCallExpr(node);
        }

        private static string StatementName(CallExpr node) => node.Callee switch
        {
            IdentifierExpr identifier => identifier.Name,
            MemberExpr member => member.Member,
            _ => "dynamic"
        };

        /// <inheritdoc />
        public override object? VisitFunctionDecl(FunctionDecl node)
        {
            Dictionary<string, TypeInfo?> previous = _locals;
            _locals = node.Params.ToDictionary(parameter => parameter.Name,
                parameter => parameter.Type is { } type ? (TypeInfo?)new(type.Name, type.IsCollection) : null,
                StringComparer.Ordinal);
            try
            {
                if (node.Body is BlockBody block)
                {
                    foreach (Statement statement in block.Statements)
                    {
                        Visit(statement);
                    }
                }

                return base.VisitFunctionDecl(node);
            }
            finally { _locals = previous; }
        }

        /// <inheritdoc />
        public override object? VisitLetDecl(LetDecl node)
        {
            Value(node.Value);
            return base.VisitLetDecl(node);
        }

        /// <inheritdoc />
        public override object? VisitLetStatement(LetStatement node)
        {
            Value(node.Value);
            base.VisitLetStatement(node);
            _locals[node.Name] = node.TypeAnnotation is { } annotation
                ? new(annotation.Name, annotation.IsCollection) : program.Model.InferType(node.Value, _locals);
            return null;
        }

        /// <inheritdoc />
        public override object? VisitForEachStatement(ForEachStatement node)
        {
            Visit(node.Collection);
            Dictionary<string, TypeInfo?> previous = _locals;
            _locals = new(previous, StringComparer.Ordinal);
            TypeInfo? collection = program.Model.InferType(node.Collection, previous);
            _locals[node.Variable] = collection is { IsCollection: true } item ? new(item.Name, false) : null;
            try { foreach (Statement statement in node.Body)
                {
                    Visit(statement);
                }
            }
            finally { _locals = previous; }
            return null;
        }

        /// <inheritdoc />
        public override object? VisitLambdaExpr(LambdaExpr node)
        {
            Dictionary<string, TypeInfo?> previous = _locals;
            _locals = new(previous, StringComparer.Ordinal);
            foreach (Parameter parameter in node.Params)
            {
                _locals[parameter.Name] = parameter.Type is { } type ? new(type.Name, type.IsCollection) : null;
            }

            try { return base.VisitLambdaExpr(node); }
            finally { _locals = previous; }
        }

        /// <inheritdoc />
        public override object? VisitFilterExpr(FilterExpr node)
        {
            if (node.Predicate is CallExpr call)
            {
                TypeInfo? receiver = program.Model.InferType(node.Collection, _locals);
                if (!node.Negated && receiver is { IsCollection: false })
                {
                    _receivers[call] = node.Collection;
                }
                else
                {
                    _unsupportedApplications.Add(call);
                }
            }
            return base.VisitFilterExpr(node);
        }

        protected override object? DefaultVisit(AstNode node)
        {
            if (node is ForEachExpr loop)
            {
                Visit(loop.Loop);
            }

            return null;
        }
    }
}
