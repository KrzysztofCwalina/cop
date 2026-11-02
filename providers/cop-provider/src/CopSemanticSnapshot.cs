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
        var file = item switch
        {
            SourceFile source => source,
            StatementInfo statement => statement.File,
            MethodDeclaration method => method.File,
            TypeDeclaration type => type.File,
            _ => null
        };
        if (file is null || file.Language != "cop" || !_attached.Add(file)) return;
        var analysis = new Lazy<bool>(() => { Analyze(file); return true; });
        foreach (var statement in file.Statements) statement.DeferSemanticAnalysis(analysis);
    }

    private StaticSourceProgram ProgramFor(SourceFile file)
    {
        var directory = Path.GetDirectoryName(file.FullPath)!;
        if (!_programs.TryGetValue(directory, out var program))
        {
            program = new(() =>
            {
                var files = new List<StaticSourceFile>();
                foreach (var path in Directory.GetFiles(directory, "*.cop").Order(StringComparer.Ordinal))
                {
                    var known = _attached.FirstOrDefault(source => source.FullPath == path);
                    var text = known?.RawText ?? File.ReadAllText(path);
                    if (CopSourceParser.IsNegativeParserFixture(path, text)) continue;
                    try { files.Add(new(CopParser.Parse(text, path), path, text)); }
                    catch (ParseException ex) { ProviderErrors.Report($"{path}({ex.Line}): {ex.Message}"); }
                }
                var feeds = new List<string>();
                for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                {
                    var packages = Path.Combine(ancestor.FullName, "packages");
                    if (Directory.Exists(packages)) feeds.Add(packages);
                }
                var result = new StaticSourceProgram(files, feeds);
                foreach (var diagnostic in result.Diagnostics)
                    ProviderErrors.Report($"{file.Path}: Cop semantic context: {diagnostic}");
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
            var module = CopParser.Parse(file.RawText, file.FullPath);
            var expressions = new Dictionary<StatementInfo, Cop.Lang.Ast.Expression>(ReferenceEqualityComparer.Instance);
            var syntax = new CopSourceParser().ParseModule(file.FullPath, file.RawText, module, expressions);
            if (syntax.Statements.Count != file.Statements.Count)
                throw new InvalidDataException("Cached Cop statement identities do not match the current syntax.");
            var sites = new Dictionary<Cop.Lang.Ast.Expression, List<StatementInfo>>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < syntax.Statements.Count; i++)
                if (expressions.TryGetValue(syntax.Statements[i], out var expression))
                {
                    if (!sites.TryGetValue(expression, out var statements)) sites.Add(expression, statements = []);
                    statements.Add(file.Statements[i]);
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
            if (!sites.TryGetValue(expression, out var statements)) return;
            var type = program.Model.InferType(expression, _locals);
            foreach (var statement in statements.Where(s => s.Kind != "call"))
                statement.ValueType = Reference(type);
        }

        public override object? VisitCallExpr(CallExpr node)
        {
            if (sites.TryGetValue(node, out var statements))
            {
                var target = node;
                TypeInfo? receiver = null;
                if (_receivers.TryGetValue(node, out var subject))
                {
                    receiver = program.Model.InferType(subject, _locals);
                    target = node with { Args = [subject, .. node.Args] };
                }
                var selected = _unsupportedApplications.Contains(node)
                    ? new StaticCallInfo(null, [], null, ["Collection/filter application requires runtime dispatch."])
                    : program.Model.ResolveCall(target, _locals);
                var location = selected.Declaration is { } function ? program.LocationOf(function) : null;
                CallableReference? callable = null;
                if (selected.Declaration is { } declaration && selected.ParameterTypes.All(type => type is not null))
                {
                    var name = location?.Namespace is { } ns && declaration.Name.StartsWith(ns + ".", StringComparison.Ordinal)
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
                foreach (var statement in statements.Where(s => s.Kind == "call"))
                {
                    statement.ReceiverType = Reference(receiver);
                    statement.ValueType = Reference(selected.ResultType);
                    statement.ReferencedCallable = callable;
                }
                foreach (var diagnostic in selected.Diagnostics)
                    ProviderWarnings.ReportOnce("cop", $"{sourcePath}:{node.Line}:{StatementName(node)}",
                        $"{sourcePath}({node.Line}): {diagnostic}");
            }
            return base.VisitCallExpr(node);
        }

        private static string StatementName(CallExpr node) => node.Callee switch
        {
            IdentifierExpr identifier => identifier.Name,
            MemberExpr member => member.Member,
            _ => "dynamic"
        };

        public override object? VisitFunctionDecl(FunctionDecl node)
        {
            var previous = _locals;
            _locals = node.Params.ToDictionary(parameter => parameter.Name,
                parameter => parameter.Type is { } type ? (TypeInfo?)new(type.Name, type.IsCollection) : null,
                StringComparer.Ordinal);
            try
            {
                if (node.Body is BlockBody block)
                    foreach (var statement in block.Statements) Visit(statement);
                return base.VisitFunctionDecl(node);
            }
            finally { _locals = previous; }
        }

        public override object? VisitLetDecl(LetDecl node)
        {
            Value(node.Value);
            return base.VisitLetDecl(node);
        }

        public override object? VisitLetStatement(LetStatement node)
        {
            Value(node.Value);
            base.VisitLetStatement(node);
            _locals[node.Name] = node.TypeAnnotation is { } annotation
                ? new(annotation.Name, annotation.IsCollection) : program.Model.InferType(node.Value, _locals);
            return null;
        }

        public override object? VisitForEachStatement(ForEachStatement node)
        {
            Visit(node.Collection);
            var previous = _locals;
            _locals = new(previous, StringComparer.Ordinal);
            var collection = program.Model.InferType(node.Collection, previous);
            _locals[node.Variable] = collection is { IsCollection: true } item ? new(item.Name, false) : null;
            try { foreach (var statement in node.Body) Visit(statement); }
            finally { _locals = previous; }
            return null;
        }

        public override object? VisitLambdaExpr(LambdaExpr node)
        {
            var previous = _locals;
            _locals = new(previous, StringComparer.Ordinal);
            foreach (var parameter in node.Params)
                _locals[parameter.Name] = parameter.Type is { } type ? new(type.Name, type.IsCollection) : null;
            try { return base.VisitLambdaExpr(node); }
            finally { _locals = previous; }
        }

        public override object? VisitFilterExpr(FilterExpr node)
        {
            if (node.Predicate is CallExpr call)
            {
                var receiver = program.Model.InferType(node.Collection, _locals);
                if (!node.Negated && receiver is { IsCollection: false }) _receivers[call] = node.Collection;
                else _unsupportedApplications.Add(call);
            }
            return base.VisitFilterExpr(node);
        }

        protected override object? DefaultVisit(AstNode node)
        {
            if (node is ForEachExpr loop) Visit(loop.Loop);
            return null;
        }
    }
}
