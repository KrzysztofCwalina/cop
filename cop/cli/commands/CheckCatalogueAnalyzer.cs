// Builds the static check inventory shown by `cop checks`.

using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Providers;

namespace Cop.Cli.Commands;

/// <summary>
/// Builds a best-effort, static inventory from parsed declarations. It never evaluates Cop
/// expressions or loads providers.
/// </summary>
internal sealed class CheckCatalogueAnalyzer
{
    private readonly string _rootPath;
    private readonly IReadOnlyList<CheckCatalogueSource> _sources;
    private readonly Dictionary<ModuleNode, ModuleInfo> _moduleInfo =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Declaration, CheckCatalogueDefinition> _definitions =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<DeclarationInfo>> _localDeclarations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DeclarationInfo>> _packageDeclarations =
        new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    private CheckCatalogueAnalyzer(
        string rootPath,
        IReadOnlyList<CheckCatalogueSource> sources)
    {
        _rootPath = rootPath;
        _sources = sources;
    }

    /// <summary>Builds the check and command inventory for the given parsed sources.</summary>
    public static CheckCatalogueAnalysis Analyze(
        string rootPath,
        IReadOnlyList<CheckCatalogueSource> sources,
        IReadOnlyList<string> importErrors)
    {
        CheckCatalogueAnalyzer analyzer = new CheckCatalogueAnalyzer(rootPath, sources);
        analyzer._warnings.AddRange(importErrors);
        return analyzer.Build();
    }

    private CheckCatalogueAnalysis Build()
    {
        foreach (CheckCatalogueSource source in _sources)
        {
            _moduleInfo[source.Module] = new ModuleInfo(source);
        }

        foreach (ModuleInfo info in _moduleInfo.Values)
        {
            foreach (Declaration declaration in info.Source.Module.Declarations)
            {
                Dictionary<string, List<DeclarationInfo>> index =
                    info.Source.PackageName is null ? _localDeclarations : _packageDeclarations;
                Add(index, declaration switch
                {
                    LetDecl let => let.Name,
                    FunctionDecl function => function.Name,
                    CommandDecl command => command.Name,
                    _ => null
                }, new DeclarationInfo(declaration, info));
            }
        }

        List<(ModuleNode Module, string FilePath)> modules = _sources
            .Select(source => (source.Module, source.FilePath))
            .ToList();
        LeafCheckCatalog leafChecks = LeafCheckCatalog.Build(modules);
        foreach (CheckCatalogueSource source in _sources)
        {
            List<Declaration> leaves = source.Module.Declarations
                .Where(declaration => leafChecks.NameOf(declaration) is not null ||
                    IsDirectViolationCall(declaration, _moduleInfo[source.Module], leafChecks))
                .ToList();
            foreach (Declaration declaration in leaves)
            {
                ModuleInfo module = _moduleInfo[source.Module];
                CheckCatalogueDefinition definition = CreateDefinition(
                    declaration,
                    module,
                    leaves.Count == 1);
                _definitions[declaration] = definition;
            }
        }

        List<CheckCatalogueCommandEntry> commands = [];
        HashSet<string> reachedChecks = new(StringComparer.Ordinal);
        foreach (ModuleInfo info in _moduleInfo.Values)
        {
            if (info.Source.PackageName is not null)
            {
                continue;
            }

            foreach (Declaration declaration in info.Source.Module.Declarations)
            {
                if (declaration is FunctionDecl { IsCommand: true } function)
                {
                    CommandExpansion expansion = new CommandExpansion(this, info);
                    expansion.ExpandCommand(function);
                    reachedChecks.UnionWith(expansion.CheckUses.Keys);
                    commands.Add(CreateCommand(info, function, expansion));
                }
                else if (declaration is CommandDecl legacyCommand)
                {
                    CommandExpansion expansion = new CommandExpansion(this, info);
                    expansion.ExpandCommand(legacyCommand);
                    reachedChecks.UnionWith(expansion.CheckUses.Keys);
                    commands.Add(CreateCommand(info, legacyCommand, expansion));
                }
            }
        }

        foreach (CheckCatalogueCommandEntry command in commands)
        {
            foreach (string unresolved in command.Unresolved)
            {
                _warnings.Add($"{command.FilePath}:{command.Line} ({command.Name}): {unresolved}");
            }
        }

        List<CheckCatalogueDefinition> checks = _definitions.Values
            .Where(definition => definition.Origin == "workspace" ||
                reachedChecks.Contains(definition.Id))
            .OrderBy(definition => definition.Origin, StringComparer.Ordinal)
            .ThenBy(definition => definition.FilePath, StringComparer.Ordinal)
            .ThenBy(definition => definition.Line)
            .ToList();

        return new CheckCatalogueAnalysis(
            checks,
            commands.OrderBy(command => command.Origin, StringComparer.Ordinal)
                .ThenBy(command => command.FilePath, StringComparer.Ordinal)
                .ThenBy(command => command.Line)
                .ThenBy(command => command.Name, StringComparer.Ordinal)
                .ToList(),
            _warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    private bool IsDirectViolationCall(
        Declaration declaration,
        ModuleInfo context,
        LeafCheckCatalog leafChecks)
    {
        if (declaration is not LetDecl { Value: CallExpr call } ||
            !CommandExpansion.TryGetTarget(call.Callee, out string name, out string? package))
        {
            return false;
        }

        IReadOnlyList<DeclarationInfo> matches = Resolve(name, context, package);
        return matches.Count > 0 && matches.All(info => leafChecks.NameOf(info.Declaration) is not null);
    }

    private CheckCatalogueDefinition CreateDefinition(
        Declaration declaration,
        ModuleInfo module,
        bool fileHasOneCheck)
    {
        string name = declaration switch
        {
            LetDecl let => let.Name,
            FunctionDecl function => function.Name,
            _ => declaration.GetType().Name
        };
        string path = DisplayPath(module);
        string origin = module.Source.PackageName ?? "workspace";
        string id = $"{origin}:{path}:{declaration.Line}:{name}";
        string? description = declaration switch
        {
            LetDecl let => let.DocComment,
            FunctionDecl function => function.DocComment,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(description) && fileHasOneCheck)
        {
            description = GetFileHeader(module.Source.Source);
        }

        string source = declaration switch
        {
            LetDecl let => $"let {let.Name} = {ExpressionRenderer.Render(let.Value, 4000)}",
            FunctionDecl { Body: ExpressionBody body } function =>
                $"function {function.Name}({string.Join(", ", function.Params.Select(parameter => parameter.Name))}) => " +
                ExpressionRenderer.Render(body.Expr, 4000),
            FunctionDecl function =>
                $"function {function.Name}({string.Join(", ", function.Params.Select(parameter => parameter.Name))}) => <block body>",
            _ => name
        };

        return new CheckCatalogueDefinition(
            id,
            name,
            path,
            origin,
            declaration.Line,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            source,
            declaration is FunctionDecl ? "function" : "let");
    }

    private CheckCatalogueCommandEntry CreateCommand(
        ModuleInfo module,
        Declaration declaration,
        CommandExpansion expansion)
    {
        string name = declaration switch
        {
            FunctionDecl function => function.Name,
            CommandDecl command => command.Name,
            _ => "unknown"
        };
        string path = DisplayPath(module);
        string origin = module.Source.PackageName ?? "workspace";
        return new CheckCatalogueCommandEntry(
            $"{origin}:{path}:{declaration.Line}:{name}",
            name,
            path,
            origin,
            declaration.Line,
            expansion.CheckUses.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            expansion.CheckUses.Values
                .SelectMany(scopes => scopes)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .ToList(),
            expansion.Unresolved.Distinct(StringComparer.Ordinal).ToList());
    }

    private string DisplayPath(ModuleInfo module)
    {
        if (module.Source.PackageName is null)
        {
            return NormalizePath(Path.GetRelativePath(_rootPath, module.Source.FilePath));
        }

        string? sourceDirectory = Path.GetDirectoryName(module.Source.FilePath);
        string? packageDirectory = sourceDirectory is not null &&
            string.Equals(Path.GetFileName(sourceDirectory), "src", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(sourceDirectory)
                : sourceDirectory;
        string relative = packageDirectory is null
            ? Path.GetFileName(module.Source.FilePath)
            : Path.GetRelativePath(packageDirectory, module.Source.FilePath);
        return $"{module.Source.PackageName}/{NormalizePath(relative)}";
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string? GetFileHeader(string source)
    {
        List<string> comments = [];
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 && comments.Count == 0)
            {
                continue;
            }
            if (!trimmed.StartsWith('#'))
            {
                break;
            }

            int prefixLength = trimmed.StartsWith("##", StringComparison.Ordinal) ? 2 : 1;
            comments.Add(trimmed[prefixLength..].Trim());
        }

        return comments.Count == 0 ? null : string.Join(System.Environment.NewLine, comments);
    }

    private static void Add(
        Dictionary<string, List<DeclarationInfo>> index,
        string? name,
        DeclarationInfo declaration)
    {
        if (name is null)
        {
            return;
        }

        if (!index.TryGetValue(name, out List<DeclarationInfo>? declarations))
        {
            declarations = [];
            index.Add(name, declarations);
        }

        declarations.Add(declaration);
    }

    private IReadOnlyList<DeclarationInfo> Resolve(
        string name,
        ModuleInfo context,
        string? qualifiedPackage = null)
    {
        if (qualifiedPackage is not null)
        {
            return _packageDeclarations.TryGetValue(name, out List<DeclarationInfo>? declarations)
                ? declarations.Where(info =>
                    string.Equals(info.Module.Source.PackageName, qualifiedPackage, StringComparison.Ordinal))
                    .Where(IsExported)
                    .ToList()
                : [];
        }

        List<DeclarationInfo> inModule = context.Source.Module.Declarations
            .Where(declaration => GetName(declaration) == name)
            .Select(declaration => new DeclarationInfo(declaration, context))
            .ToList();
        if (inModule.Count > 0)
        {
            return inModule;
        }

        if (context.Source.PackageName is null)
        {
            List<DeclarationInfo> workspace = _localDeclarations.GetValueOrDefault(name) ?? [];
            if (workspace.Count > 0)
            {
                return workspace;
            }
        }
        else
        {
            List<DeclarationInfo> samePackage = _packageDeclarations.GetValueOrDefault(name) ?? [];
            samePackage = samePackage.Where(info =>
                string.Equals(info.Module.Source.PackageName, context.Source.PackageName, StringComparison.Ordinal))
                .ToList();
            if (samePackage.Count > 0)
            {
                return samePackage;
            }
        }

        HashSet<string> visiblePackages = GetImportedPackages(context);
        return (_packageDeclarations.GetValueOrDefault(name) ?? [])
            .Where(info => visiblePackages.Contains(info.Module.Source.PackageName ?? ""))
            .Where(IsExported)
            .ToList();
    }

    private HashSet<string> GetImportedPackages(ModuleInfo context)
    {
        HashSet<string> result = [];
        Queue<string> pending = new(context.Source.Module.Declarations
            .OfType<ImportDecl>()
            .Select(import => import.ModuleName));
        while (pending.TryDequeue(out string? packageName))
        {
            if (!result.Add(packageName))
            {
                continue;
            }

            foreach (ModuleInfo importedModule in _moduleInfo.Values.Where(info =>
                string.Equals(info.Source.PackageName, packageName, StringComparison.Ordinal)))
            {
                foreach (ImportDecl import in importedModule.Source.Module.Declarations.OfType<ImportDecl>())
                {
                    pending.Enqueue(import.ModuleName);
                }
            }
        }

        return result;
    }

    private static bool IsExported(DeclarationInfo info) => info.Declaration switch
    {
        LetDecl let => let.IsExported,
        FunctionDecl function => function.IsExported,
        CommandDecl command => command.IsExported,
        _ => false
    };

    private static string? GetName(Declaration declaration) => declaration switch
    {
        LetDecl let => let.Name,
        FunctionDecl function => function.Name,
        CommandDecl command => command.Name,
        _ => null
    };

    private sealed class ModuleInfo(CheckCatalogueSource source)
    {
        public CheckCatalogueSource Source { get; } = source;
    }

    private sealed record DeclarationInfo(Declaration Declaration, ModuleInfo Module);

    private sealed class CommandExpansion(CheckCatalogueAnalyzer owner, ModuleInfo commandModule)
    {
        private static readonly IReadOnlyDictionary<string, BoundExpression> s_noParameters =
            new Dictionary<string, BoundExpression>(StringComparer.Ordinal);
        private readonly HashSet<Declaration> _activeFunctions = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<FunctionDecl> _activeCommandHelpers = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, HashSet<string>> CheckUses { get; } = new(StringComparer.Ordinal);
        public List<string> Unresolved { get; } = [];

        public void ExpandCommand(FunctionDecl command)
        {
            if (command.Body is BlockBody block)
            {
                foreach (Statement statement in block.Statements)
                {
                    ExpandCommandBody(statement, commandModule, s_noParameters);
                }
            }
            else if (command.Body is ExpressionBody expression)
            {
                ExpandCommandBody(expression.Expr, commandModule, s_noParameters);
            }
        }

        public void ExpandCommand(CommandDecl command)
        {
            foreach (Statement statement in command.Body)
            {
                ExpandCommandBody(statement, commandModule, s_noParameters);
            }
        }

        private void ExpandCommandBody(
            AstNode node,
            ModuleInfo context,
            IReadOnlyDictionary<string, BoundExpression> parameters)
        {
            new CommandBodyWalker(call => ExpandCommandCall(call, context, parameters)).Visit(node);
        }

        private void ExpandCommandCall(
            CallExpr call,
            ModuleInfo context,
            IReadOnlyDictionary<string, BoundExpression> parameters)
        {
            if (call.Callee is IdentifierExpr { Name: "CHECK" })
            {
                ExpandCheckCall(call, context, parameters);
                return;
            }

            if (!TryGetTarget(call.Callee, out string name, out string? package) ||
                package is not null)
            {
                return;
            }

            DeclarationInfo[] matches = owner.Resolve(name, context)
                .Where(info => info.Module.Source.PackageName is null &&
                    info.Declaration is FunctionDecl function &&
                    function.Params.Count == call.Args.Count)
                .ToArray();
            if (matches.Length != 1 || matches[0].Declaration is not FunctionDecl helper)
            {
                return;
            }

            if (!_activeCommandHelpers.Add(helper))
            {
                Unresolved.Add($"Recursive command helper composition through '{name}'.");
                return;
            }

            try
            {
                Dictionary<string, BoundExpression> helperParameters = helper.Params
                    .Select((parameter, index) => (parameter.Name, call.Args[index]))
                    .ToDictionary(
                        item => item.Name,
                        item => new BoundExpression(item.Item2, context, parameters),
                        StringComparer.Ordinal);
                if (helper.Body is ExpressionBody expression)
                {
                    ExpandCommandBody(expression.Expr, matches[0].Module, helperParameters);
                }
                else if (helper.Body is BlockBody block)
                {
                    foreach (Statement statement in block.Statements)
                    {
                        ExpandCommandBody(statement, matches[0].Module, helperParameters);
                    }
                }
            }
            finally
            {
                _activeCommandHelpers.Remove(helper);
            }
        }

        private void ExpandCheckCall(
            CallExpr call,
            ModuleInfo context,
            IReadOnlyDictionary<string, BoundExpression> parameters)
        {
            if (call.Args.Count == 0)
            {
                Unresolved.Add($"CHECK at line {call.Line} has no argument.");
                return;
            }

            foreach (Expression argument in call.Args)
            {
                Expand(argument, context, parameters,
                    new HashSet<Declaration>(ReferenceEqualityComparer.Instance), null);
            }
        }

        private void Expand(
            Expression expression,
            ModuleInfo context,
            IReadOnlyDictionary<string, BoundExpression> parameters,
            HashSet<Declaration> activeDeclarations,
            string? inheritedScope)
        {
            switch (expression)
            {
                case IdentifierExpr identifier when parameters.TryGetValue(identifier.Name, out BoundExpression? bound) &&
                    bound is not null:
                    Expand(bound.Expression, bound.Module, bound.Parameters,
                        activeDeclarations, inheritedScope);
                    return;

                case IdentifierExpr identifier:
                    ExpandReference(identifier.Name, context, parameters, activeDeclarations, inheritedScope);
                    return;

                case MemberExpr { Object: IdentifierExpr package, Member: string member }:
                    ExpandReference(member, context, parameters, activeDeclarations, inheritedScope, package.Name);
                    return;

                case BinaryExpr { Op: BinaryOp.Add } addition:
                    Expand(addition.Left, context, parameters, activeDeclarations, inheritedScope);
                    Expand(addition.Right, context, parameters, activeDeclarations, inheritedScope);
                    return;

                case CallExpr { Callee: IdentifierExpr { Name: "CHECK" } } nestedCheck:
                    foreach (Expression argument in nestedCheck.Args)
                    {
                        Expand(argument, context, parameters, activeDeclarations, inheritedScope);
                    }
                    return;

                case CallExpr call:
                    ExpandCall(call, context, parameters, activeDeclarations, inheritedScope);
                    return;

                case ConditionalExpr conditional:
                    Unresolved.Add(
                        $"Conditional check membership at line {conditional.Line}: " +
                        $"{ExpressionRenderer.Render(conditional.Condition)}");
                    Expand(conditional.Then, context, parameters, activeDeclarations,
                        $"conditional: {ExpressionRenderer.Render(conditional.Condition)}");
                    Expand(conditional.Else, context, parameters, activeDeclarations,
                        $"conditional: {ExpressionRenderer.Render(conditional.Condition)}");
                    return;

                case ListExpr list:
                    foreach (Expression item in list.Elements)
                    {
                        Expand(item, context, parameters, activeDeclarations, inheritedScope);
                    }
                    return;

                default:
                    Unresolved.Add(
                        $"Cannot resolve check expression at line {expression.Line}: " +
                        ExpressionRenderer.Render(expression, 300));
                    return;
            }
        }

        private void ExpandReference(
            string name,
            ModuleInfo context,
            IReadOnlyDictionary<string, BoundExpression> parameters,
            HashSet<Declaration> activeDeclarations,
            string? inheritedScope,
            string? qualifiedPackage = null)
        {
            IReadOnlyList<DeclarationInfo> matches = owner.Resolve(name, context, qualifiedPackage);
            if (matches.Count == 0)
            {
                Unresolved.Add($"Cannot resolve '{name}' from {owner.DisplayPath(context)}.");
                return;
            }
            if (matches.Count > 1)
            {
                Unresolved.Add($"'{name}' is ambiguous in {owner.DisplayPath(context)}.");
                return;
            }

            DeclarationInfo match = matches[0];
            if (owner._definitions.TryGetValue(match.Declaration, out CheckCatalogueDefinition? check))
            {
                AddCheck(check, inheritedScope);
                return;
            }

            if (!activeDeclarations.Add(match.Declaration))
            {
                Unresolved.Add($"Cyclic check composition through '{name}'.");
                return;
            }

            try
            {
                if (match.Declaration is LetDecl let)
                {
                    Expand(let.Value, match.Module, parameters, activeDeclarations, inheritedScope);
                }
                else
                {
                    Unresolved.Add($"'{name}' does not statically resolve to a check.");
                }
            }
            finally
            {
                activeDeclarations.Remove(match.Declaration);
            }
        }

        private void ExpandCall(
            CallExpr call,
            ModuleInfo context,
            IReadOnlyDictionary<string, BoundExpression> parameters,
            HashSet<Declaration> activeDeclarations,
            string? inheritedScope)
        {
            if (!TryGetTarget(call.Callee, out string name, out string? package))
            {
                Unresolved.Add($"Unsupported check call at line {call.Line}: {ExpressionRenderer.Render(call, 300)}");
                return;
            }

            IReadOnlyList<DeclarationInfo> matches = owner.Resolve(name, context, package);
            if (matches.Count == 0)
            {
                Unresolved.Add($"Cannot resolve check function '{name}' from {owner.DisplayPath(context)}.");
                return;
            }
            if (matches.Count > 1)
            {
                Unresolved.Add($"Check function '{name}' is ambiguous in {owner.DisplayPath(context)}.");
                return;
            }

            DeclarationInfo match = matches[0];
            if (owner._definitions.TryGetValue(match.Declaration, out CheckCatalogueDefinition? check))
            {
                string scope = call.Args.Count == 0
                    ? inheritedScope ?? "no arguments"
                    : string.Join(", ", call.Args.Select(argument =>
                        RenderScopedExpression(argument, context, parameters)));
                AddCheck(check, scope);
                return;
            }

            if (match.Declaration is not FunctionDecl function)
            {
                Unresolved.Add($"'{name}' is not a statically resolvable check function.");
                return;
            }
            if (!_activeFunctions.Add(function))
            {
                Unresolved.Add($"Recursive check composition through '{name}'.");
                return;
            }

            try
            {
                if (function.Body is ExpressionBody body && function.Params.Count == call.Args.Count)
                {
                    Dictionary<string, BoundExpression> functionParameters = function.Params
                        .Select((parameter, index) => (parameter.Name, call.Args[index]))
                        .ToDictionary(
                            item => item.Name,
                            item => new BoundExpression(item.Item2, context, parameters),
                            StringComparer.Ordinal);
                    Expand(body.Expr, match.Module, functionParameters, activeDeclarations,
                        inheritedScope ?? string.Join(", ", call.Args.Select(argument =>
                            RenderScopedExpression(argument, context, parameters))));
                }
                else
                {
                    Unresolved.Add(
                        $"Cannot statically expand '{name}' at line {call.Line}; " +
                        "its body or arguments are not a simple expression.");
                }
            }
            finally
            {
                _activeFunctions.Remove(function);
            }
        }

        internal static bool TryGetTarget(
            Expression callee,
            out string name,
            out string? package)
        {
            switch (callee)
            {
                case IdentifierExpr identifier:
                    name = identifier.Name;
                    package = null;
                    return true;
                case MemberExpr { Object: IdentifierExpr packageIdentifier } member:
                    name = member.Member;
                    package = packageIdentifier.Name;
                    return true;
                default:
                    name = "";
                    package = null;
                    return false;
            }
        }

        private void AddCheck(CheckCatalogueDefinition check, string? scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                if (!CheckUses.ContainsKey(check.Id))
                {
                    CheckUses.Add(check.Id, new HashSet<string>(StringComparer.Ordinal));
                }
                return;
            }

            if (!CheckUses.TryGetValue(check.Id, out HashSet<string>? scopes))
            {
                scopes = new HashSet<string>(StringComparer.Ordinal);
                CheckUses.Add(check.Id, scopes);
            }

            scopes.Add(scope);
        }

        private string RenderScopedExpression(
            Expression expression,
            ModuleInfo context,
            IReadOnlyDictionary<string, BoundExpression> parameters)
        {
            if (expression is IdentifierExpr identifier &&
                parameters.TryGetValue(identifier.Name, out BoundExpression? bound))
            {
                return RenderScopedExpression(bound.Expression, bound.Module, bound.Parameters);
            }

            if (expression is IdentifierExpr reference)
            {
                IReadOnlyList<DeclarationInfo> matches = owner.Resolve(reference.Name, context);
                if (matches.Count == 1 && matches[0].Declaration is LetDecl let)
                {
                    return $"{reference.Name} = {ExpressionRenderer.Render(let.Value, 400)}";
                }
            }

            if (expression is MemberExpr member)
            {
                return $"{RenderScopedExpression(member.Object, context, parameters)}.{member.Member}";
            }

            return ExpressionRenderer.Render(expression);
        }

        private sealed record BoundExpression(
            Expression Expression,
            ModuleInfo Module,
            IReadOnlyDictionary<string, BoundExpression> Parameters);

        private sealed class CommandBodyWalker(Action<CallExpr> onCall) : AstWalker
        {
            public override object? VisitCallExpr(CallExpr node)
            {
                if (node.Callee is IdentifierExpr { Name: "CHECK" })
                {
                    onCall(node);
                    return node;
                }

                onCall(node);
                return base.VisitCallExpr(node);
            }
        }
    }
}
