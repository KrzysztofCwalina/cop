// Extracts C# declarations, expressions, and statement relationships with Roslyn.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Cop.Providers.SourceModel;
using CheckTypeKind = Cop.Providers.SourceModel.TypeKind;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.CompilerServices;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Extracts C# declarations and statement relationships using Roslyn.
/// </summary>
public class CSharpSourceParser : ISourceParser
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => [".cs"];
    /// <inheritdoc />
    public override string Language => "csharp";

    internal static CSharpParseOptions ParseOptions { get; } = CSharpParseOptions.Default
        .WithFeatures([KeyValuePair.Create("FileBasedProgram", "true")]);

    private static readonly ConditionalWeakTable<SyntaxTree, SyntaxIndex> s_syntaxIndexes = new();

    // Syntax trees are immutable. Reuse the relevant nodes across syntax extraction and
    // deferred semantic passes without keeping completed provider snapshots alive.
    private sealed class SyntaxIndex
    {
        internal List<UsingDirectiveSyntax> Usings { get; } = [];
        internal List<TypeDeclarationSyntax> Types { get; } = [];
        internal Dictionary<int, CatchClauseSyntax> Catches { get; } = [];
        internal List<BaseObjectCreationExpressionSyntax> Creations { get; } = [];
        internal List<ExpressionSyntax> Calls { get; } = [];
        internal List<SyntaxNode> SemanticNodes { get; } = [];

        internal SyntaxIndex(SyntaxTree tree)
        {
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case UsingDirectiveSyntax directive:
                        Usings.Add(directive);
                        break;
                    case TypeDeclarationSyntax declaration:
                        Types.Add(declaration);
                        break;
                    case CatchClauseSyntax clause:
                        Catches.Add(clause.SpanStart, clause);
                        break;
                    case BaseObjectCreationExpressionSyntax creation:
                        Creations.Add(creation);
                        Calls.Add(creation);
                        SemanticNodes.Add(creation);
                        break;
                    case InvocationExpressionSyntax invocation:
                        Calls.Add(invocation);
                        SemanticNodes.Add(invocation);
                        break;
                    case LocalDeclarationStatementSyntax declaration when declaration.Declaration.Type.IsVar:
                        SemanticNodes.Add(declaration);
                        break;
                }
            }
        }
    }

    private static SyntaxIndex GetSyntaxIndex(SyntaxTree tree) =>
        s_syntaxIndexes.GetValue(tree, static key => new SyntaxIndex(key));

    /// <inheritdoc />
    public override ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Parse(filePath, sourceText));
    }

    /// <summary>Parses in-memory C# syntax without starting asynchronous provider work.</summary>
    public SourceFile? Parse(string filePath, string sourceText)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText, ParseOptions, path: filePath);
        return ParseWithSemantics(filePath, sourceText, tree, model: null);
    }

    /// <summary>
    /// Parses a C# file using a pre-built syntax tree and optional semantic model.
    /// When a semantic model is provided, type resolution uses Roslyn's semantic APIs
    /// instead of string-based heuristics.
    /// </summary>
    public SourceFile? ParseWithSemantics(string filePath, string sourceText, SyntaxTree tree, SemanticModel? model)
    {
        if (model is not null && !ReferenceEquals(model.SyntaxTree, tree))
        {
            throw new ArgumentException(
                "The semantic model must belong to the supplied syntax tree.", nameof(model));
        }

        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        SyntaxIndex syntax = GetSyntaxIndex(tree);

        List<TypeDeclaration> types = new List<TypeDeclaration>();

        // Extract using directives
        List<string> usings = syntax.Usings
            .Select(u => u.Name?.ToString() ?? u.ToString().TrimEnd(';').Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();

        // Single-pass: extract namespace and top-level types in one traversal
        string? ns = null;
        foreach (SyntaxNode node in root.DescendantNodes(node =>
                     node is not BaseTypeDeclarationSyntax && node is not GlobalStatementSyntax))
        {
            switch (node)
            {
                case BaseNamespaceDeclarationSyntax nsDecl when ns is null:
                    ns = nsDecl.Name.ToString();
                    break;
                case ClassDeclarationSyntax or StructDeclarationSyntax
                    or InterfaceDeclarationSyntax or RecordDeclarationSyntax:
                    if (node.Parent is not TypeDeclarationSyntax)
                    {
                        types.Add(ExtractType((TypeDeclarationSyntax)node));
                    }

                    break;
                case EnumDeclarationSyntax enumDecl:
                    if (enumDecl.Parent is not TypeDeclarationSyntax)
                    {
                        types.Add(ExtractEnum(enumDecl));
                    }

                    break;
            }
        }

        // Collect all statements from methods (already built as trees during type extraction)
        // plus global and assembly-level statements
        List<StatementInfo> allStatements = new List<StatementInfo>();

        // Flatten method statements from all types (already constructed)
        foreach (TypeDeclaration type in types)
        {
            FlattenTypeStatements(type, allStatements);
        }

        // Extract top-level (global) statements
        foreach (GlobalStatementSyntax globalStmt in root.Members.OfType<GlobalStatementSyntax>())
        {
            List<StatementInfo> statements = new List<StatementInfo>();
            ExtractStatement(globalStmt.Statement, statements, isInMethod: false, method: null, parent: null);
            FlattenStatements(statements, allStatements);
        }

        // Extract assembly-level attributes as statements
        foreach (AttributeListSyntax attrList in root.AttributeLists)
        {
            if (attrList.Target?.Identifier.Text != "assembly")
            {
                continue;
            }

            foreach (AttributeSyntax attr in attrList.Attributes)
            {
                string attrName = attr.Name.ToString().Replace("Attribute", string.Empty);
                List<string> args = attr.ArgumentList?.Arguments
                    .Select(a => a.Expression.ToString().Trim('"')).ToList() ?? [];
                allStatements.Add(new CSharpStatementInfo(new StatementInfo("attribute", [], attrName, null, args, LineOf(attrList), false)));
            }
        }

        AddNestedCalls(root, types, allStatements);
        Lazy<SemanticModel> standaloneModel = new(() =>
            model ?? CSharpCompilation.Create("CatchFilterFacts", [tree],
                FrameworkMetadata.CompilationReferences).GetSemanticModel(tree));
        DeferCatchFilterFacts(tree, allStatements, () => standaloneModel.Value);
        DeferCancellationFacts(tree, allStatements, () => standaloneModel.Value);
        DeferOwnershipFacts(tree, allStatements, () => standaloneModel.Value);
        (List<RegionInfo>? regions, HashSet<int>? commentLines, List<CommentInfo> comments) =
            ExtractTrivia(root, sourceText, allStatements);
        allStatements = allStatements.OrderBy(s => s.Line)
            .ThenBy(s => (s as CSharpStatementInfo)?.SyntaxStart ?? -1).ToList();

        // Enrich with semantic information when model is available
        if (model != null)
        {
            EnrichWithSemantics(tree, model, types, allStatements);
            DeferBoundFacts(tree, allStatements, () => model);
        }

        return new SourceFile(filePath, "csharp", types, allStatements, sourceText)
        {
            Usings = usings,
            Imports = syntax.Usings.Select(u => new ImportInfo(u.Name?.ToString() ?? string.Empty, LineOf(u))
            {
                Alias = u.Alias?.Name.Identifier.ValueText,
                IsStatic = u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword),
                Column = ColumnOf(u), EndLine = EndLineOf(u), EndColumn = EndColumnOf(u)
            }).ToList(),
            Namespace = ns,
            Regions = regions,
            CommentLines = commentLines,
            Comments = comments,
            ParseErrors = CollectSyntaxErrors(filePath, tree)
        };
    }

    /// <summary>
    /// Collects Roslyn SYNTAX errors (parse-level only — taken from the syntax tree, not a semantic
    /// model, so missing-reference noise is excluded). A genuinely malformed C# file is reported
    /// instead of being silently modelled as partial/incorrect data.
    /// </summary>
    private static List<string> CollectSyntaxErrors(string filePath, SyntaxTree tree)
    {
        List<string> errors = new List<string>();
        foreach (Diagnostic diag in tree.GetDiagnostics())
        {
            if (diag.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            LinePosition pos = diag.Location.GetLineSpan().StartLinePosition;
            errors.Add($"{filePath}({pos.Line + 1},{pos.Character + 1}): error: {diag.GetMessage()} ({diag.Id})");
        }
        return errors;
    }

    /// <summary>
    /// Enriches already-extracted types and statements with semantic information from the model.
    /// Called after syntax extraction to add interface lists and accurate type resolution.
    /// </summary>
    public static void EnrichWithSemantics(SyntaxTree tree, SemanticModel model, List<TypeDeclaration> types, List<StatementInfo> allStatements)
        => EnrichWithSemantics(tree, model, types, allStatements, typeNamesOnly: false);

    internal static void EnrichInterfaces(SyntaxTree tree, SemanticModel model,
        List<TypeDeclaration> types, List<StatementInfo> statements)
        => EnrichWithSemantics(tree, model, types, statements, typeNamesOnly: false,
            resolveInvocations: false, resolveInferences: false);

    /// <summary>
    /// Resolves statement type names using the supplied semantic compilation models.
    /// </summary>
    public static void EnrichTypeNames(SyntaxTree tree, SemanticModel model, List<StatementInfo> statements)
        => EnrichWithSemantics(tree, model, [], statements, typeNamesOnly: true);

    /// <summary>
    /// Resolves statement type names using the supplied semantic compilation models.
    /// </summary>
    public static void EnrichTypeNames(SyntaxTree tree, SemanticModel invocationModel,
        SemanticModel inferenceModel, List<StatementInfo> statements)
        => EnrichWithSemantics(tree, invocationModel, [], statements, typeNamesOnly: true, inferenceModel);

    /// <summary>
    /// Defers invocation and inference binding until the corresponding facts are
    /// requested.
    /// </summary>
    public static void DeferTypeNames(SyntaxTree tree, List<StatementInfo> statements,
        Func<SemanticModel> invocationModel, Func<SemanticModel> inferenceModel)
    {
        // Roslyn models own their bound-node caches; separate models repeat binding and nullable flow.
        Lazy<SemanticModel> nullableModel = new(inferenceModel);
        DeferBoundFacts(tree, statements, () => nullableModel.Value);
        Lazy<bool> invocations = new Lazy<bool>(() =>
        {
            EnrichWithSemantics(tree, invocationModel(), [], statements, typeNamesOnly: true,
                resolveInferences: false);
            return true;
        });
        Lazy<bool> inferences = new Lazy<bool>(() =>
        {
            EnrichWithSemantics(tree, nullableModel.Value, [], statements, typeNamesOnly: true,
                resolveInvocations: false);
            return true;
        });
        foreach (StatementInfo statement in statements)
        {
            statement.DeferTypeNameAnalysis(statement is CSharpStatementInfo { RequiresTypeNameAnalysis: true }
                ? statement.Kind == "declaration" ? inferences : invocations
                : null);
        }
    }

    private static void DeferBoundFacts(SyntaxTree tree, List<StatementInfo> statements,
        Func<SemanticModel> getModel)
    {
        DeferCatchFilterFacts(tree, statements, getModel);
        DeferCancellationFacts(tree, statements, getModel);
        DeferOwnershipFacts(tree, statements, getModel);
        Lazy<SemanticModel> model = new(getModel);
        Lazy<bool> diagnostics = new(() =>
        {
            ReportSemanticErrors(tree, model.Value);
            return true;
        });
        foreach (IGrouping<(int, int?, int?), CSharpStatementInfo> group in statements.OfType<CSharpStatementInfo>()
                     .GroupBy(statement => (statement.SyntaxStart, statement.EndLine, statement.EndColumn)))
        {
            CSharpStatementInfo[] matches = group.ToArray();
            Lazy<bool> binding = new(() =>
            {
                _ = diagnostics.Value;
                if (group.Key is (int start, int endLine, int endColumn) && start >= 0)
                {
                    int end = tree.GetText().Lines.GetPosition(new(endLine - 1, endColumn - 1));
                    TextSpan span = TextSpan.FromBounds(start, end);
                    SyntaxNode node = tree.GetRoot().FindNode(span, getInnermostNodeForTie: true);
                    if (node.Span == span)
                    {
                        EnrichBoundFacts(node, model.Value, matches);
                    }
                }
                return true;
            });
            foreach (CSharpStatementInfo statement in matches)
            {
                statement.DeferSemanticAnalysis(binding);
            }
        }
    }

    private static void DeferCatchFilterFacts(SyntaxTree tree, List<StatementInfo> statements,
        Func<SemanticModel> getModel)
    {
        CSharpStatementInfo[] catches = statements.OfType<CSharpStatementInfo>()
            .Where(statement => statement.HasCatchFilter).ToArray();
        if (catches.Length == 0)
        {
            return;
        }

        Lazy<SemanticModel> model = new(getModel);
        Dictionary<int, CatchClauseSyntax> clauses = GetSyntaxIndex(tree).Catches;
        foreach (CSharpStatementInfo statement in catches)
        {
            if (clauses.TryGetValue(statement.SyntaxStart, out CatchClauseSyntax? clause))
            {
                statement.DeferredCatchFilterAnalysis =
                    new(() => CSharpCatchFilterFacts.IsTypeRestricted(clause, model.Value));
            }
        }
    }

    private static void DeferCancellationFacts(SyntaxTree tree, List<StatementInfo> statements,
        Func<SemanticModel> getModel)
    {
        Lazy<SemanticModel> model = new(getModel);
        Dictionary<int, CatchClauseSyntax> clauses = GetSyntaxIndex(tree).Catches;
        foreach (CSharpStatementInfo statement in statements.OfType<CSharpStatementInfo>()
                     .Where(statement => statement.Kind == "catch"))
        {
            if (clauses.TryGetValue(statement.SyntaxStart, out CatchClauseSyntax? clause))
            {
                statement.DeferredCancellationAnalysis =
                    new(() => CSharpCancellationFacts.Analyze(clause, model.Value));
            }
        }
    }

    private static void DeferOwnershipFacts(SyntaxTree tree, List<StatementInfo> statements,
        Func<SemanticModel> getModel)
    {
        Dictionary<int, CSharpStatementInfo[]> calls = statements
            .OfType<CSharpStatementInfo>().Where(statement => statement.Kind == "call")
            .GroupBy(statement => statement.SyntaxStart)
            .ToDictionary(group => group.Key, group => group.ToArray());
        Lazy<SemanticModel> model = new(getModel);
        foreach (BaseObjectCreationExpressionSyntax creation in GetSyntaxIndex(tree).Creations)
        {
            if (!calls.TryGetValue(creation.SpanStart, out CSharpStatementInfo[]? matches))
            {
                continue;
            }

            foreach (CSharpStatementInfo statement in matches)
            {
                statement.DeferredOwnershipAnalysis =
                    new(() => CSharpResourceOwnershipFacts.IsTransferred(creation, model.Value));
            }
        }
    }

    private static void ReportSemanticErrors(SyntaxTree tree, SemanticModel model)
    {
        foreach (Diagnostic? diagnostic in model.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            LinePosition position = diagnostic.Location.GetLineSpan().StartLinePosition;
            Cop.Core.ProviderErrors.Report(
                $"{tree.FilePath}({position.Line + 1},{position.Character + 1}): " +
                $"C# semantic context: {diagnostic.GetMessage()} ({diagnostic.Id})");
        }
    }

    private static void EnrichBoundFacts(SyntaxNode node, SemanticModel model, CSharpStatementInfo[] matches)
    {
        ExpressionSyntax? expression = node switch
        {
            VariableDeclaratorSyntax variable => variable.Initializer?.Value,
            ReturnStatementSyntax returned => returned.Expression,
            ExpressionSyntax value => value,
            _ => null
        };
        if (expression is null)
        {
            return;
        }

        TypeReference? resultType = BoundType(model.GetTypeInfo(expression).Type);
        IMethodSymbol? callable = expression is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
            ? model.GetSymbolInfo(expression).Symbol as IMethodSymbol : null;
        ITypeSymbol? receiver = expression switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
                when model.GetSymbolInfo(member.Expression).Symbol is not INamedTypeSymbol
                => model.GetTypeInfo(member.Expression).Type,
            InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax }
                => model.GetTypeInfo(expression.Ancestors().OfType<ConditionalAccessExpressionSyntax>().First().Expression).Type,
            InvocationExpressionSyntax invocation when callable?.MethodKind == MethodKind.DelegateInvoke
                => model.GetTypeInfo(invocation.Expression).Type,
            InvocationExpressionSyntax when callable is { IsStatic: false, MethodKind: MethodKind.Ordinary }
                => model.GetEnclosingSymbol(expression.SpanStart)?.ContainingType,
            _ => null
        };
        CallableReference? selected = callable is null || callable.ContainingType?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error
            ? null : BoundCallable(callable);
        foreach (CSharpStatementInfo statement in matches)
        {
            statement.ValueType = resultType;
            if (node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
            {
                statement.ReceiverType = BoundType(receiver);
                statement.ReferencedCallable = selected;
            }
        }
    }

    private static TypeReference? BoundType(ITypeSymbol? symbol)
    {
        if (symbol is null || symbol.TypeKind is Microsoft.CodeAnalysis.TypeKind.Error or Microsoft.CodeAnalysis.TypeKind.Dynamic)
        {
            return null;
        }

        if (symbol is IArrayTypeSymbol array)
        {
            return BoundType(array.ElementType) is { } element
                ? element with { OriginalText = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) } : null;
        }

        if (symbol is IPointerTypeSymbol pointer)
        {
            return BoundType(pointer.PointedAtType) is { } element
                ? element with { OriginalText = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) } : null;
        }

        string? ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString() : null;
        List<TypeReference> arguments = new List<TypeReference>();
        if (symbol is INamedTypeSymbol named)
        {
            foreach (ITypeSymbol argument in named.TypeArguments)
            {
                if (BoundType(argument) is not { } bound)
                {
                    return null;
                }

                arguments.Add(bound);
            }
        }

        return new TypeReference(symbol.Name, ns, arguments,
            symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
        {
            IsAnonymous = symbol.IsAnonymousType
        };
    }

    private static CallableReference? BoundCallable(IMethodSymbol symbol)
    {
        List<TypeReference> parameters = new List<TypeReference>();
        foreach (IParameterSymbol parameter in symbol.Parameters)
        {
            if (BoundType(parameter.Type) is not { } bound)
            {
                return null;
            }

            parameters.Add(bound);
        }
        Location? location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        FileLinePositionSpan? span = location?.GetLineSpan();
        return new CallableReference(symbol.Name, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
        {
            DeclaringType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Namespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            ReturnType = BoundType(symbol.ReturnType),
            Parameters = parameters,
            SourceFile = span?.Path,
            Line = span?.StartLinePosition.Line + 1,
            Column = span?.StartLinePosition.Character + 1,
            EndLine = span?.EndLinePosition.Line + 1,
            EndColumn = span?.EndLinePosition.Character + 1
        };
    }

    private static void EnrichWithSemantics(SyntaxTree tree, SemanticModel model,
        List<TypeDeclaration> types, List<StatementInfo> allStatements, bool typeNamesOnly,
        SemanticModel? inferenceModel = null, bool resolveInvocations = true, bool resolveInferences = true)
    {
        SyntaxIndex syntax = GetSyntaxIndex(tree);

        Dictionary<int, TypeDeclaration> typesByStart = new Dictionary<int, TypeDeclaration>();
        void AddTypes(IEnumerable<TypeDeclaration> declarations)
        {
            foreach (TypeDeclaration type in declarations)
            {
                if (type is CSharpTypeDeclaration cs)
                {
                    typesByStart[cs.SyntaxStart] = type;
                }

                AddTypes(type.NestedTypes);
            }
        }
        AddTypes(types);

        // Enrich type declarations with interface information
        foreach (TypeDeclarationSyntax node in typeNamesOnly ? Enumerable.Empty<TypeDeclarationSyntax>()
                     : syntax.Types)
        {
            if (typesByStart.TryGetValue(node.SpanStart, out TypeDeclaration? typeDecl))
            {
                INamedTypeSymbol? symbol = model.GetDeclaredSymbol(node);
                if (symbol is INamedTypeSymbol namedType)
                {
                    typeDecl.Interfaces = namedType.AllInterfaces
                        .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                        .ToList();
                }
            }
        }

        // Enrich object-creation statements with constructed type interfaces
        Dictionary<int, List<StatementInfo>> stmtsByLine = new Dictionary<int, List<StatementInfo>>();
        foreach (StatementInfo s in allStatements)
        {
            if (!stmtsByLine.TryGetValue(s.Line, out List<StatementInfo>? list))
            {
                list = [];
                stmtsByLine[s.Line] = list;
            }
            list.Add(s);
        }

        foreach (SyntaxNode node in syntax.SemanticNodes)
        {
            if (node is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            {
                if (typeNamesOnly)
                {
                    continue;
                }

                int line = LineOf(node);
                if (stmtsByLine.TryGetValue(line, out List<StatementInfo>? stmts))
                {
                    TypeInfo typeInfo = model.GetTypeInfo(node);
                    if (typeInfo.Type is INamedTypeSymbol createdType)
                    {
                        List<string> interfaces = createdType.AllInterfaces
                            .Select(i => i.Name)
                            .ToList();

                        // Match to the statement that has a "new" kind or contains this type
                        foreach (StatementInfo stmt in stmts)
                        {
                            if (stmt is CSharpStatementInfo cs && cs.SyntaxStart == node.SpanStart
                                && (stmt.Kind == "new" || stmt.Kind == "call"))
                            {
                                stmt.ConstructedTypeInterfaces = interfaces;
                                if (node is ImplicitObjectCreationExpressionSyntax)
                                {
                                    stmt.TypeName = createdType.Name;
                                }

                                break;
                            }
                        }
                    }
                }
            }
            // Resolve method call type names from semantic model
            else if (resolveInvocations && node is InvocationExpressionSyntax invocation)
            {
                int line = LineOf(node);
                if (stmtsByLine.TryGetValue(line, out List<StatementInfo>? stmts))
                {
                    SymbolInfo symbolInfo = model.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
                    {
                        string? containingType = methodSymbol.ContainingType?.Name;
                        if (containingType != null)
                        {
                            foreach (StatementInfo stmt in stmts)
                            {
                                if (stmt is CSharpStatementInfo cs
                                    && cs.SyntaxStart == invocation.SpanStart
                                    && (stmt.Kind == "call" || stmt.Kind == "await"))
                                {
                                    stmt.TypeName = containingType;
                                }
                            }
                        }
                    }
                }
            }
            // Resolve the inferred type for `var` declarations, so `var` is never a TypeName.
            // The `var` keyword stays in Keywords; TypeName becomes the actual inferred type.
            else if (resolveInferences && node is LocalDeclarationStatementSyntax varDecl && varDecl.Declaration.Type.IsVar)
            {
                int line = LineOf(node);
                if (stmtsByLine.TryGetValue(line, out List<StatementInfo>? stmts))
                {
                    string? inferred = (inferenceModel ?? model).GetTypeInfo(varDecl.Declaration.Type).Type
                        ?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                        ?.TrimEnd('?');
                    if (!string.IsNullOrEmpty(inferred) && inferred != "?" && inferred != "var")
                    {
                        // Match by variable name so multiple declarations on one line stay distinct.
                        HashSet<int> starts = varDecl.Declaration.Variables.Select(v => v.SpanStart).ToHashSet();
                        foreach (StatementInfo stmt in stmts)
                        {
                            if (stmt is CSharpStatementInfo cs && stmt.Kind == "declaration" && starts.Contains(cs.SyntaxStart))
                            {
                                stmt.TypeName = inferred;
                            }
                        }
                    }
                }
            }
        }
    }

    private static void FlattenTypeStatements(TypeDeclaration type, List<StatementInfo> allStatements)
    {
        foreach (MethodDeclaration ctor in type.Constructors)
        {
            FlattenStatements(ctor.Statements, allStatements);
        }

        foreach (MethodDeclaration method in type.Methods)
        {
            FlattenStatements(method.Statements, allStatements);
        }

        foreach (TypeDeclaration nested in type.NestedTypes)
        {
            FlattenTypeStatements(nested, allStatements);
        }
    }

    private static void FlattenStatements(List<StatementInfo> statements, List<StatementInfo> target)
    {
        foreach (StatementInfo stmt in statements)
        {
            target.Add(stmt);
            if (stmt.MutableChildren.Count > 0)
            {
                FlattenStatements(stmt.MutableChildren, target);
            }
        }
    }

    private static TypeDeclaration ExtractType(TypeDeclarationSyntax syntax)
    {
        CheckTypeKind kind = syntax switch
        {
            ClassDeclarationSyntax => CheckTypeKind.Class,
            StructDeclarationSyntax => CheckTypeKind.Struct,
            InterfaceDeclarationSyntax => CheckTypeKind.Interface,
            RecordDeclarationSyntax r =>
                r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? CheckTypeKind.Struct : CheckTypeKind.Class,
            _ => CheckTypeKind.Class
        };

        Modifier modifiers = ExtractModifiers(syntax.Modifiers);
        List<string> baseTypes = syntax.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [];
        List<string> decorators = syntax.AttributeLists
            .SelectMany(a => a.Attributes)
            .Select(a => a.Name.ToString()).ToList();

        List<MethodDeclaration> constructors = new List<MethodDeclaration>();
        List<MethodDeclaration> methods = new List<MethodDeclaration>();
        List<TypeDeclaration> nestedTypes = new List<TypeDeclaration>();
        List<FieldDeclaration> fields = new List<FieldDeclaration>();
        List<PropertyDeclaration> properties = new List<PropertyDeclaration>();
        List<EventDeclaration> events = new List<EventDeclaration>();

        foreach (MemberDeclarationSyntax? member in syntax.Members.SelectMany(member =>
                     member is ExtensionBlockDeclarationSyntax extension
                         ? extension.Members.AsEnumerable() : [member]))
        {
            if (member is ConstructorDeclarationSyntax ctor)
            {
                constructors.Add(ExtractConstructor(ctor));
            }
            else if (member is MethodDeclarationSyntax method)
            {
                methods.Add(ExtractMethod(method));
            }
            else if (member is FieldDeclarationSyntax field)
            {
                fields.AddRange(ExtractFields(field));
            }
            else if (member is PropertyDeclarationSyntax prop)
            {
                properties.Add(ExtractProperty(prop));
            }
            else if (member is EventFieldDeclarationSyntax eventField)
            {
                events.AddRange(ExtractEventFields(eventField));
            }
            else if (member is EventDeclarationSyntax eventDecl)
            {
                events.Add(ExtractEvent(eventDecl));
            }
            else if (member is TypeDeclarationSyntax nestedType)
            {
                nestedTypes.Add(ExtractType(nestedType));
            }
            else if (member is EnumDeclarationSyntax nestedEnum)
            {
                nestedTypes.Add(ExtractEnum(nestedEnum));
            }

            if (member is not BaseTypeDeclarationSyntax)
            {
                foreach (SyntaxNode? callable in member.DescendantNodesAndSelf()
                             .Where(n => n is AccessorDeclarationSyntax or LocalFunctionStatementSyntax
                                 or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax))
                {
                    methods.Add(ExtractCallable(callable));
                }

                if (member is PropertyDeclarationSyntax { ExpressionBody: { } body } property)
                {
                    methods.Add(ExtractCallable(property, "get_" + property.Identifier.ValueText, body.Expression));
                }

                if (member is IndexerDeclarationSyntax { ExpressionBody: { } indexerBody } indexer)
                {
                    methods.Add(ExtractCallable(indexer, "get_Item", indexerBody.Expression));
                }
            }
        }

        CSharpTypeDeclaration result = new CSharpTypeDeclaration(new TypeDeclaration(
            syntax.Identifier.Text, kind, modifiers, baseTypes, decorators,
            constructors, methods, nestedTypes, [], LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            Fields = fields,
            Properties = properties,
            Events = events,
            Namespace = NamespaceOf(syntax),
            TypeParameters = syntax.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [],
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        })
        {
            IsRecord = syntax is RecordDeclarationSyntax,
            IsRecordStruct = syntax is RecordDeclarationSyntax rds && rds.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword),
            IsReadOnly = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)),
            IsRef = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)),
            IsFileLocal = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)),
            IsPartial = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
            HasPrimaryConstructor = syntax.ParameterList != null,
            IsGeneric = syntax.TypeParameterList != null
        };
        result.SyntaxStart = syntax.SpanStart;
        return result;
    }

    private static TypeDeclaration ExtractEnum(EnumDeclarationSyntax syntax) =>
        new CSharpTypeDeclaration(new TypeDeclaration(syntax.Identifier.Text, CheckTypeKind.Enum, ExtractModifiers(syntax.Modifiers),
            syntax.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [],
            [], [], [], [],
            syntax.Members.Select(m => m.Identifier.Text).ToList(),
            LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            Namespace = NamespaceOf(syntax),
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        })
        {
            IsFileLocal = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword))
        };

    private static MethodDeclaration ExtractConstructor(ConstructorDeclarationSyntax syntax)
    {
        CSharpMethodDeclaration method = new CSharpMethodDeclaration(new MethodDeclaration(".ctor", ExtractModifiers(syntax.Modifiers), [],
            null, syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax))
        {
            Kind = "constructor",
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        })
        {
            IsExpressionBodied = syntax.ExpressionBody != null
        };
        method.SyntaxStart = syntax.SpanStart;
        List<StatementInfo> methodStatements = new List<StatementInfo>();
        if (syntax.Body != null)
        {
            ExtractStatements(syntax.Body, methodStatements, isInMethod: true, method: method, parent: null);
        }

        if (syntax.ExpressionBody != null)
        {
            ExtractExpressionStatement(syntax.ExpressionBody.Expression, methodStatements,
                LineOf(syntax.ExpressionBody), isInMethod: true, method: method, parent: null);
        }

        method.Statements = methodStatements;
        return method;
    }

    private static CSharpMethodDeclaration ExtractCallable(SyntaxNode syntax, string? name = null, ExpressionSyntax? expression = null)
    {
        string? receiver = ExtensionReceiver(syntax);
        (string? callableName, ParameterListSyntax? parameters, TypeSyntax? returnType, BlockSyntax? body, ArrowExpressionClauseSyntax? arrow) = syntax switch
        {
            LocalFunctionStatementSyntax local => (local.Identifier.ValueText, local.ParameterList,
                (TypeSyntax?)local.ReturnType, local.Body, local.ExpressionBody),
            AccessorDeclarationSyntax accessor => (accessor.Keyword.ValueText + "_" +
                (accessor.Parent?.Parent is PropertyDeclarationSyntax property ? property.Identifier.ValueText : "Item"),
                null, null, accessor.Body, accessor.ExpressionBody),
            OperatorDeclarationSyntax op => ("operator " + op.OperatorToken.ValueText, op.ParameterList,
                op.ReturnType, op.Body, op.ExpressionBody),
            ConversionOperatorDeclarationSyntax conversion => ("operator " + conversion.Type, conversion.ParameterList,
                conversion.Type, conversion.Body, conversion.ExpressionBody),
            DestructorDeclarationSyntax destructor => ("~" + destructor.Identifier.ValueText, destructor.ParameterList,
                null, destructor.Body, destructor.ExpressionBody),
            _ => (name!, null, null, (BlockSyntax?)null, (ArrowExpressionClauseSyntax?)null)
        };
        SyntaxTokenList modifiers = syntax switch
        {
            LocalFunctionStatementSyntax localFunction => localFunction.Modifiers,
            AccessorDeclarationSyntax accessor => accessor.Modifiers,
            BaseMethodDeclarationSyntax callable => callable.Modifiers,
            _ => default
        };
        CSharpMethodDeclaration result = new CSharpMethodDeclaration(new MethodDeclaration(callableName, ExtractModifiers(modifiers), [],
            returnType is null ? null : ExtractTypeReference(returnType),
            parameters?.Parameters.Select(ExtractParameter).ToList() ?? [], LineOf(syntax))
        {
            Kind = syntax switch
            {
                LocalFunctionStatementSyntax => "localFunction",
                AccessorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax =>
                    "accessor",
                OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
                DestructorDeclarationSyntax => "destructor",
                _ => "method"
            },
            ReceiverType = receiver,
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            TypeParameters = syntax is LocalFunctionStatementSyntax genericLocal
                ? genericLocal.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [] : [],
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        })
        {
            IsExtension = receiver is not null,
            IsExpressionBodied = arrow is not null || expression is not null,
            IsGeneric = syntax is LocalFunctionStatementSyntax { TypeParameterList: not null }
        };
        result.SyntaxStart = syntax.SpanStart;
        if (body is not null)
        {
            ExtractStatements(body, result.Statements, true, result);
        }

        if ((expression ?? arrow?.Expression) is { } value)
        {
            ExtractExpressionStatement(value, result.Statements, LineOf(value), true, result);
        }

        return result;
    }

    private static MethodDeclaration ExtractMethod(MethodDeclarationSyntax syntax)
    {
        string? receiver = ExtensionReceiver(syntax);
        CSharpMethodDeclaration method = new CSharpMethodDeclaration(new MethodDeclaration(syntax.Identifier.Text, ExtractModifiers(syntax.Modifiers),
            syntax.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList(),
            ExtractTypeReference(syntax.ReturnType),
            syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax))
        {
            ReceiverType = receiver,
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            TypeParameters = syntax.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [],
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        })
        {
            IsExtension = receiver is not null || syntax.ParameterList.Parameters.FirstOrDefault()?.Modifiers
                .Any(m => m.IsKind(SyntaxKind.ThisKeyword)) == true,
            IsPartial = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
            IsUnsafe = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)),
            IsExtern = syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)),
            IsExpressionBodied = syntax.ExpressionBody != null,
            IsGeneric = syntax.TypeParameterList != null
        };
        method.SyntaxStart = syntax.SpanStart;
        List<StatementInfo> methodStatements = new List<StatementInfo>();
        if (syntax.Body != null)
        {
            ExtractStatements(syntax.Body, methodStatements, isInMethod: true, method: method, parent: null);
        }

        if (syntax.ExpressionBody != null)
        {
            ExtractExpressionStatement(syntax.ExpressionBody.Expression, methodStatements,
                LineOf(syntax.ExpressionBody), isInMethod: true, method: method, parent: null);
        }

        method.Statements = methodStatements;
        return method;
    }

    private static string? ExtensionReceiver(SyntaxNode syntax)
    {
        SyntaxNode? parent = syntax is AccessorDeclarationSyntax
            ? syntax.Parent?.Parent?.Parent : syntax.Parent;
        return parent is ExtensionBlockDeclarationSyntax { ParameterList: { } parameters }
            ? parameters.Parameters.FirstOrDefault()?.Type?.ToString() : null;
    }

    private static List<FieldDeclaration> ExtractFields(FieldDeclarationSyntax syntax)
    {
        Modifier modifiers = ExtractModifiers(syntax.Modifiers);
        TypeReference type = ExtractTypeReference(syntax.Declaration.Type);
        return syntax.Declaration.Variables.Select(v =>
            new FieldDeclaration(v.Identifier.Text, type, modifiers, LineOf(v))
        ).ToList();
    }

    private static PropertyDeclaration ExtractProperty(PropertyDeclarationSyntax syntax)
    {
        bool hasGetter = syntax.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? syntax.ExpressionBody != null;
        bool hasSetter = syntax.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) ?? false;
        return new PropertyDeclaration(
            syntax.Identifier.Text, ExtractTypeReference(syntax.Type),
            ExtractModifiers(syntax.Modifiers), LineOf(syntax))
        {
            HasGetter = hasGetter,
            HasSetter = hasSetter,
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax)
        };
    }

    private static EventDeclaration ExtractEvent(EventDeclarationSyntax syntax) =>
        new(syntax.Identifier.Text, ExtractTypeReference(syntax.Type),
            ExtractModifiers(syntax.Modifiers), LineOf(syntax));

    private static List<EventDeclaration> ExtractEventFields(EventFieldDeclarationSyntax syntax)
    {
        Modifier modifiers = ExtractModifiers(syntax.Modifiers);
        TypeReference? type = syntax.Declaration.Type is not null ? ExtractTypeReference(syntax.Declaration.Type) : null;
        return syntax.Declaration.Variables.Select(v =>
            new EventDeclaration(v.Identifier.Text, type, modifiers, LineOf(v))
        ).ToList();
    }

    private static ParameterDeclaration ExtractParameter(ParameterSyntax syntax) =>
        new(syntax.Identifier.Text, syntax.Type is not null ? ExtractTypeReference(syntax.Type) : null,
            syntax.Modifiers.Any(SyntaxKind.ParamsKeyword), false,
            syntax.Default is not null, LineOf(syntax))
        {
            DefaultValueText = syntax.Default?.Value.ToString(), IsOptional = syntax.Default is not null,
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        };

    private static TypeReference ExtractTypeReference(TypeSyntax typeSyntax)
    {
        string originalText = typeSyntax.ToString();
        string? ns = null;

        switch (typeSyntax)
        {
            case QualifiedNameSyntax qualified:
                ns = qualified.Left.ToString();
                return new TypeReference(qualified.Right.Identifier.Text, ns,
                    ExtractGenericArgs(qualified.Right), originalText);
            case GenericNameSyntax generic:
                return new TypeReference(generic.Identifier.Text, null,
                    ExtractGenericArgs(generic), originalText);
            case NullableTypeSyntax nullable:
                TypeReference inner = ExtractTypeReference(nullable.ElementType);
                return new TypeReference(inner.Name, inner.Namespace,
                    inner.GenericArguments, originalText);
            case ArrayTypeSyntax array:
                TypeReference elemRef = ExtractTypeReference(array.ElementType);
                return new TypeReference(elemRef.Name, elemRef.Namespace,
                    elemRef.GenericArguments, originalText);
            case AliasQualifiedNameSyntax alias:
                return new TypeReference(alias.Name.Identifier.Text, alias.Alias.ToString(),
                    ExtractGenericArgs(alias.Name), originalText);
            default:
                string name = typeSyntax switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    PredefinedTypeSyntax pre => pre.Keyword.Text,
                    _ => originalText
                };
                return new TypeReference(name, null, [], originalText);
        }
    }

    private static List<TypeReference> ExtractGenericArgs(SimpleNameSyntax nameSyntax)
    {
        if (nameSyntax is GenericNameSyntax generic)
        {
            return generic.TypeArgumentList.Arguments.Select(ExtractTypeReference).ToList();
        }

        return [];
    }

    private static Modifier ExtractModifiers(SyntaxTokenList modifiers)
    {
        Modifier result = Modifier.None;
        foreach (SyntaxToken mod in modifiers)
        {
            result |= mod.Kind() switch
            {
                SyntaxKind.PublicKeyword => Modifier.Public,
                SyntaxKind.PrivateKeyword => Modifier.Private,
                SyntaxKind.ProtectedKeyword => Modifier.Protected,
                SyntaxKind.InternalKeyword => Modifier.Internal,
                SyntaxKind.StaticKeyword => Modifier.Static,
                SyntaxKind.SealedKeyword => Modifier.Sealed,
                SyntaxKind.AbstractKeyword => Modifier.Abstract,
                SyntaxKind.VirtualKeyword => Modifier.Virtual,
                SyntaxKind.AsyncKeyword => Modifier.Async,
                SyntaxKind.OverrideKeyword => Modifier.Override,
                SyntaxKind.ReadOnlyKeyword => Modifier.Readonly,
                SyntaxKind.ConstKeyword => Modifier.Const,
                _ => Modifier.None
            };
        }
        return result;
    }

    private static void ExtractStatements(BlockSyntax block, List<StatementInfo> results,
        bool isInMethod = true, MethodDeclaration? method = null, StatementInfo? parent = null)
    {
        foreach (StatementSyntax stmt in block.Statements)
        {
            ExtractStatement(stmt, results, isInMethod, method, parent);
        }
    }

    /// <summary>
    /// Extract a statement body — handles both block and single-statement (non-block) bodies.
    /// </summary>
    private static void ExtractStatementBody(StatementSyntax body, List<StatementInfo> results,
        bool isInMethod, MethodDeclaration? method, StatementInfo? parent)
    {
        if (body is BlockSyntax block)
        {
            ExtractStatements(block, results, isInMethod, method, parent);
        }
        else
        {
            ExtractStatement(body, results, isInMethod, method, parent);
        }
    }

    private static void ExtractStatement(StatementSyntax stmt, List<StatementInfo> results,
        bool isInMethod = true, MethodDeclaration? method = null, StatementInfo? parent = null)
    {
        int first = results.Count;
        int line = LineOf(stmt);
        switch (stmt)
        {
            case EmptyStatementSyntax:
                results.Add(new CSharpStatementInfo(new StatementInfo("empty", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent });
                break;
            case LocalDeclarationStatementSyntax decl:
            {
                List<string> keywords = [];
                // `var` is a keyword, not a type name — record it in Keywords and leave
                // TypeName empty here; the inferred type is filled in by EnrichWithSemantics.
                bool isVar = decl.Declaration.Type.IsVar;
                string typeName = isVar ? string.Empty : decl.Declaration.Type.ToString();
                if (isVar)
                    {
                        keywords.Add("var");
                    }

                    if (typeName == "dynamic")
                    {
                        keywords.Add("dynamic");
                    }

                    foreach (VariableDeclaratorSyntax v in decl.Declaration.Variables)
                {
                        CSharpStatementInfo declStmt = new CSharpStatementInfo(new StatementInfo("declaration", keywords, typeName, v.Identifier.Text, [], line, isInMethod), v)
                    {
                        Method = method, Parent = parent,
                        RequiresTypeNameAnalysis = isVar,
                        Expression = v.Initializer?.Value.ToString(),
                        ValueExpression = v.Initializer is null ? null : ExtractExpression(v.Initializer.Value)
                    };
                    results.Add(declStmt);
                    // Also extract expressions from initializers (e.g., await, invocations)
                    if (v.Initializer?.Value != null)
                        {
                            ExtractExpressionStatement(v.Initializer.Value, results, line, isInMethod, method, parent);
                        }
                    }
                break;
            }
            case ExpressionStatementSyntax exprStmt:
                ExtractExpressionStatement(exprStmt.Expression, results, line, isInMethod, method, parent);
                if (results.Count == first)
                {
                    results.Add(new CSharpStatementInfo(new StatementInfo("expression", [], null, null, [], line, isInMethod), exprStmt.Expression)
                    {
                        Method = method, Parent = parent, Expression = exprStmt.Expression.ToString(),
                        ValueExpression = ExtractExpression(exprStmt.Expression)
                    });
                }

                break;
            case ThrowStatementSyntax throwStmt:
            {
                string? typeName = throwStmt.Expression is ObjectCreationExpressionSyntax creation
                    ? creation.Type.ToString() : null;
                    CSharpStatementInfo throwInfo = new CSharpStatementInfo(new StatementInfo("throw", [], typeName, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(throwInfo);
                break;
            }
            case ReturnStatementSyntax returnStmt:
            {
                    CSharpStatementInfo retInfo = new CSharpStatementInfo(new StatementInfo("return", [], null, null, [], line, isInMethod), stmt)
                {
                    Method = method, Parent = parent,
                    Expression = returnStmt.Expression?.ToString(),
                    ValueExpression = returnStmt.Expression is null ? null : ExtractExpression(returnStmt.Expression)
                };
                results.Add(retInfo);
                if (returnStmt.Expression != null)
                    {
                        ExtractExpressionStatement(returnStmt.Expression, results, line, isInMethod, method, parent);
                    }

                    break;
            }
            case UsingStatementSyntax usingStmt:
            {
                    CSharpStatementInfo usingInfo = new CSharpStatementInfo(new StatementInfo("using", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(usingInfo);
                if (usingStmt.Statement != null)
                    {
                        ExtractStatementBody(usingStmt.Statement, usingInfo.MutableChildren, isInMethod, method, usingInfo);
                    }

                    break;
            }
            case ForEachStatementSyntax forEach:
            {
                    CSharpStatementInfo feInfo = new CSharpStatementInfo(new StatementInfo("foreach", [], forEach.Type.ToString(),
                    forEach.Identifier.Text, [], line, isInMethod), stmt)
                {
                    Method = method, Parent = parent,
                    Condition = forEach.Expression.ToString(),
                    IsBraced = forEach.Statement is BlockSyntax,
                    IsAwaitForeach = forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                };
                results.Add(feInfo);
                ExtractStatementBody(forEach.Statement, feInfo.MutableChildren, isInMethod, method, feInfo);
                break;
            }
            case TryStatementSyntax tryStmt:
            {
                    CSharpStatementInfo tryInfo = new CSharpStatementInfo(new StatementInfo("try", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(tryInfo);
                if (tryStmt.Block != null)
                    {
                        ExtractStatements(tryStmt.Block, tryInfo.MutableChildren, isInMethod, method, tryInfo);
                    }

                    foreach (CatchClauseSyntax c in tryStmt.Catches)
                {
                    string? caughtType = c.Declaration?.Type.ToString();
                    bool hasRethrow = c.Block.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax
                            && n is not LocalFunctionStatementSyntax && n is not CatchClauseSyntax)
                        .OfType<ThrowStatementSyntax>().Any(t => t.Expression is null);
                    bool isGeneric = caughtType is null or "Exception" or "System.Exception";
                        CSharpStatementInfo catchInfo = new CSharpStatementInfo(new StatementInfo("catch", [], caughtType, null, [], LineOf(c), isInMethod), c)
                    {
                        HasRethrow = hasRethrow, IsErrorHandler = true, IsGenericErrorHandler = isGeneric,
                        Method = method, Parent = tryInfo,
                        HasCatchFilter = c.Filter != null,
                        IsOriginalExceptionAlwaysRethrown =
                            CSharpCatchRethrowFacts.AlwaysRethrows(c),
                        SyntaxStart = c.SpanStart
                    };
                    tryInfo.MutableChildren.Add(catchInfo);
                    ExtractStatements(c.Block, catchInfo.MutableChildren, isInMethod, method, catchInfo);
                }
                if (tryStmt.Finally is { } finallyClause)
                {
                        CSharpStatementInfo finallyInfo = new CSharpStatementInfo(new StatementInfo("finally", [], null, null, [], LineOf(finallyClause), isInMethod), finallyClause)
                        { Method = method, Parent = tryInfo, SyntaxStart = finallyClause.SpanStart };
                    tryInfo.MutableChildren.Add(finallyInfo);
                    ExtractStatements(finallyClause.Block, finallyInfo.MutableChildren, isInMethod, method, finallyInfo);
                }
                break;
            }
            case IfStatementSyntax ifStmt:
            {
                    CSharpStatementInfo ifInfo = new CSharpStatementInfo(new StatementInfo("if", [], null, null, [], line, isInMethod), stmt)
                {
                    Method = method, Parent = parent,
                    Condition = ifStmt.Condition.ToString(),
                    ConditionExpression = ExtractExpression(ifStmt.Condition),
                    IsBraced = ifStmt.Statement is BlockSyntax
                };
                results.Add(ifInfo);
                ExtractStatementBody(ifStmt.Statement, ifInfo.MutableChildren, isInMethod, method, ifInfo);
                if (ifStmt.Else != null)
                {
                        // else is a child of the if-statement so that statements inside else
                        // have the if (with its Condition) as an ancestor — enables guard checks
                        CSharpStatementInfo elseInfo = new CSharpStatementInfo(new StatementInfo("else", [], null, null, [], LineOf(ifStmt.Else), isInMethod), ifStmt.Else)
                    {
                        Method = method, Parent = ifInfo,
                        IsBraced = ifStmt.Else.Statement is BlockSyntax
                    };
                    ifInfo.MutableChildren.Add(elseInfo);
                    ExtractStatementBody(ifStmt.Else.Statement, elseInfo.MutableChildren, isInMethod, method, elseInfo);
                }
                break;
            }
            case WhileStatementSyntax ws:
            {
                    CSharpStatementInfo whileInfo = new CSharpStatementInfo(new StatementInfo("while", [], null, null, [], line, isInMethod), stmt)
                {
                    Method = method, Parent = parent,
                    Condition = ws.Condition.ToString(),
                    ConditionExpression = ExtractExpression(ws.Condition),
                    IsBraced = ws.Statement is BlockSyntax
                };
                results.Add(whileInfo);
                ExtractStatementBody(ws.Statement, whileInfo.MutableChildren, isInMethod, method, whileInfo);
                break;
            }
            case ForStatementSyntax fs:
            {
                    CSharpStatementInfo forInfo = new CSharpStatementInfo(new StatementInfo("for", [], null, null, [], line, isInMethod), stmt)
                {
                    Method = method, Parent = parent,
                    Condition = fs.Condition?.ToString(),
                    ConditionExpression = fs.Condition is null ? null : ExtractExpression(fs.Condition),
                    IsBraced = fs.Statement is BlockSyntax
                };
                results.Add(forInfo);
                ExtractStatementBody(fs.Statement, forInfo.MutableChildren, isInMethod, method, forInfo);
                break;
            }
            case SwitchStatementSyntax sw:
            {
                    CSharpStatementInfo switchInfo = new CSharpStatementInfo(new StatementInfo("switch", [], null, null, [], line, isInMethod), stmt)
                {
                    Method = method, Parent = parent,
                    Expression = sw.Expression.ToString()
                };
                results.Add(switchInfo);
                foreach (SwitchSectionSyntax section in sw.Sections)
                    {
                        foreach (StatementSyntax sectionStmt in section.Statements)
                        {
                            ExtractStatement(sectionStmt, switchInfo.MutableChildren, isInMethod, method, switchInfo);
                        }
                    }

                    break;
            }
            case BlockSyntax nested:
                ExtractStatements(nested, results, isInMethod, method, parent);
                break;
            case LockStatementSyntax lockStmt:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("lock", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent, Expression = lockStmt.Expression.ToString() };
                results.Add(info);
                ExtractStatementBody(lockStmt.Statement, info.MutableChildren, isInMethod, method, info);
                break;
            }
            case UnsafeStatementSyntax unsafeStmt:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("unsafe", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                if (unsafeStmt.Block != null)
                    {
                        ExtractStatements(unsafeStmt.Block, info.MutableChildren, isInMethod, method, info);
                    }

                    break;
            }
            case FixedStatementSyntax fixedStmt:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("fixed", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                ExtractStatementBody(fixedStmt.Statement, info.MutableChildren, isInMethod, method, info);
                break;
            }
            case CheckedStatementSyntax checkedStmt:
            {
                bool isUnchecked = checkedStmt.Keyword.IsKind(SyntaxKind.UncheckedKeyword);
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo(isUnchecked ? "unchecked" : "checked", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                if (checkedStmt.Block != null)
                    {
                        ExtractStatements(checkedStmt.Block, info.MutableChildren, isInMethod, method, info);
                    }

                    break;
            }
            case YieldStatementSyntax yieldStmt:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("yield", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent, Expression = yieldStmt.Expression?.ToString() };
                results.Add(info);
                if (yieldStmt.Expression != null)
                    {
                        ExtractExpressionStatement(yieldStmt.Expression, results, line, isInMethod, method, parent);
                    }

                    break;
            }
            case GotoStatementSyntax:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("goto", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                break;
            }
            case DoStatementSyntax loop:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("do", [], null, null, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent, Condition = loop.Condition.ToString(), IsBraced = loop.Statement is BlockSyntax };
                results.Add(info);
                ExtractStatementBody(loop.Statement, info.MutableChildren, isInMethod, method, info);
                break;
            }
            case ForEachVariableStatementSyntax loop:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("foreach", [], null, null, [], line, isInMethod), stmt)
                {
                    Method = method, Parent = parent, Expression = loop.Variable.ToString(),
                    Condition = loop.Expression.ToString(), IsBraced = loop.Statement is BlockSyntax,
                    IsAwaitForeach = loop.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                };
                results.Add(info);
                ExtractStatementBody(loop.Statement, info.MutableChildren, isInMethod, method, info);
                break;
            }
            case LabeledStatementSyntax label:
            {
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("label", [], null, label.Identifier.ValueText, [], line, isInMethod), stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                ExtractStatementBody(label.Statement, info.MutableChildren, isInMethod, method, info);
                break;
            }
            case BreakStatementSyntax or ContinueStatementSyntax:
                results.Add(new CSharpStatementInfo(new StatementInfo(stmt is BreakStatementSyntax ? "break" : "continue",
                    [], null, null, [], line, isInMethod), stmt) { Method = method, Parent = parent });
                break;
            case LocalFunctionStatementSyntax local:
                results.Add(new CSharpStatementInfo(new StatementInfo("localfunction", [], local.ReturnType.ToString(),
                    local.Identifier.ValueText, [], line, isInMethod), stmt) { Method = method, Parent = parent });
                break;
        }
        for (int i = first; i < results.Count; i++)
        {
            if (results[i] is CSharpStatementInfo { SyntaxStart: < 0 } info)
            {
                info.SyntaxStart = stmt.SpanStart;
            }
        }
    }

    private static void ExtractExpressionStatement(ExpressionSyntax expr, List<StatementInfo> results,
        int line, bool isInMethod = true, MethodDeclaration? method = null, StatementInfo? parent = null)
    {
        switch (expr)
        {
            case InvocationExpressionSyntax invocation:
            {
                string? typeName = null;
                string? memberName = null;
                if (invocation.Expression is MemberAccessExpressionSyntax ma)
                {
                    typeName = ma.Expression.ToString();
                    memberName = ma.Name.Identifier.Text;
                }
                else if (invocation.Expression is SimpleNameSyntax id)
                {
                    memberName = id.Identifier.Text;
                }
                else if (invocation.Expression is MemberBindingExpressionSyntax binding)
                {
                    memberName = binding.Name.Identifier.ValueText;
                    typeName = invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression.ToString();
                }
                    List<string> args = invocation.ArgumentList.Arguments
                    .Select(a => a.ToString()).ToList();
                    CSharpStatementInfo callInfo = new CSharpStatementInfo(new StatementInfo("call", [], typeName, memberName, args, LineOf(invocation), isInMethod), invocation)
                {
                    Method = method, Parent = parent,
                    SyntaxStart = invocation.SpanStart,
                    RequiresTypeNameAnalysis = true,
                    CallTarget = ExtractExpression(invocation.Expression),
                    ArgumentExpressions = invocation.ArgumentList.Arguments.Select(a => ExtractExpression(a.Expression)).ToList(),
                    Expression = invocation.ToString()
                };
                results.Add(callInfo);
                break;
            }
            case AwaitExpressionSyntax awaitExpr:
            {
                (string? awaitType, string? awaitMember, List<string>? awaitArgs) = ExtractInvocationParts(awaitExpr.Expression);
                    CSharpStatementInfo awaitInfo = new CSharpStatementInfo(new StatementInfo("await", [], awaitType, awaitMember, awaitArgs, line, isInMethod), awaitExpr)
                {
                    Method = method, Parent = parent,
                    SyntaxStart = awaitExpr.Expression.SpanStart,
                    RequiresTypeNameAnalysis = awaitExpr.Expression is InvocationExpressionSyntax,
                    Expression = awaitExpr.Expression.ToString()
                };
                results.Add(awaitInfo);
                ExtractExpressionStatement(awaitExpr.Expression, results, line, isInMethod, method, parent);
                break;
            }
            case AssignmentExpressionSyntax assignment:
            {
                    string? name = assignment.Left switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => null
                };
                    CSharpStatementInfo info = new CSharpStatementInfo(new StatementInfo("assignment", [], null, name, [], line, isInMethod), assignment)
                {
                    Method = method, Parent = parent, Expression = assignment.ToString(),
                    ValueExpression = ExtractExpression(assignment.Right)
                };
                results.Add(info);
                ExtractExpressionStatement(assignment.Right, info.MutableChildren, line, isInMethod, method, info);
                break;
            }
            case ObjectCreationExpressionSyntax objCreate:
            {
                    string ctorType = ExtractBaseTypeName(objCreate.Type);
                    List<string> args = objCreate.ArgumentList?.Arguments
                    .Select(a => a.ToString()).ToList() ?? [];
                    CSharpStatementInfo ctorInfo = new CSharpStatementInfo(new StatementInfo("call", [], ctorType, null, args, line, isInMethod), objCreate)
                {
                    Method = method, Parent = parent,
                    SyntaxStart = objCreate.SpanStart,
                    ArgumentExpressions = objCreate.ArgumentList?.Arguments.Select(a => ExtractExpression(a.Expression)).ToList() ?? [],
                    Expression = objCreate.ToString()
                };
                results.Add(ctorInfo);
                break;
            }
        }
    }

    private static ExpressionInfo ExtractExpression(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax p => ExtractExpression(p.Expression),
        LiteralExpressionSyntax literal when literal.Token.Value is string value =>
            new("stringLiteral") { StringValue = value },
        LiteralExpressionSyntax literal when literal.Token.Value is bool value =>
            new("booleanLiteral") { BooleanValue = value },
        BinaryExpressionSyntax binary => new("binary")
        {
            Name = binary.Kind().ToString(),
            Children = [ExtractExpression(binary.Left), ExtractExpression(binary.Right)]
        },
        IdentifierNameSyntax id => new("identifier") { Name = id.Identifier.ValueText },
        GenericNameSyntax name => new("identifier") { Name = name.Identifier.ValueText },
        MemberAccessExpressionSyntax member => new("memberAccess")
        {
            Name = member.Name.Identifier.ValueText, Target = ExtractExpression(member.Expression)
        },
        MemberBindingExpressionSyntax member => new("memberAccess")
        {
            Name = member.Name.Identifier.ValueText,
            Target = ExtractConditionalReceiver(member)
        },
        InvocationExpressionSyntax call => new("invocation")
        {
            Target = ExtractExpression(call.Expression),
            Children = call.ArgumentList.Arguments.Select(a => ExtractExpression(a.Expression)).ToList()
        },
        _ => new("other")
    };

    private static ExpressionInfo? ExtractConditionalReceiver(MemberBindingExpressionSyntax member)
    {
        for (SyntaxNode? child = member; child?.Parent is { } parent; child = parent)
        {
            if (parent is ConditionalAccessExpressionSyntax conditional
                && ReferenceEquals(child, conditional.WhenNotNull))
            {
                return ExtractExpression(conditional.Expression);
            }
        }
        return null;
    }

    private static void AddNestedCalls(CompilationUnitSyntax root, List<TypeDeclaration> types, List<StatementInfo> statements)
    {
        Dictionary<(int Start, int? EndLine, int? EndColumn), CSharpStatementInfo> bySpan =
            statements.OfType<CSharpStatementInfo>()
            .Where(s => s.SyntaxStart >= 0)
            .GroupBy(s => (s.SyntaxStart, s.EndLine, s.EndColumn))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Kind == "call" ? 0 : 1).First());
        static (int Start, int? EndLine, int? EndColumn) SpanOf(SyntaxNode syntax)
        {
            LinePosition end = syntax.GetLocation().GetLineSpan().EndLinePosition;
            return (syntax.SpanStart, end.Line + 1, end.Character + 1);
        }

        Dictionary<int, MethodDeclaration> methods = new Dictionary<int, MethodDeclaration>();
        void AddMethods(TypeDeclaration type)
        {
            foreach (MethodDeclaration? method in type.Methods.Concat(type.Constructors))
            {
                if (method is CSharpMethodDeclaration cs)
                {
                    methods.TryAdd(cs.SyntaxStart, method);
                }
            }

            foreach (TypeDeclaration nested in type.NestedTypes)
            {
                AddMethods(nested);
            }
        }
        foreach (TypeDeclaration type in types)
        {
            AddMethods(type);
        }

        // Statement extraction already covers direct calls. Walk syntax to include calls in
        // conditions, field initializers, arguments, and other nested expressions exactly once.
        foreach (ExpressionSyntax invocation in GetSyntaxIndex(root.SyntaxTree).Calls)
        {
            if (bySpan.TryGetValue(SpanOf(invocation), out CSharpStatementInfo? existing)
                && existing.Kind == "call")
            {
                continue;
            }

            CSharpStatementInfo? parent = invocation.Ancestors()
                .Select(n => bySpan.GetValueOrDefault(SpanOf(n)))
                .FirstOrDefault(s => s is not null);
            MethodDeclaration? method = invocation.Ancestors().Select(n => methods.GetValueOrDefault(n.SpanStart))
                .FirstOrDefault(m => m is not null);
            List<StatementInfo> extracted = new List<StatementInfo>();
            if (invocation is ImplicitObjectCreationExpressionSyntax implicitCreation)
            {
                extracted.Add(new CSharpStatementInfo(new StatementInfo("call", [], null, null,
                    implicitCreation.ArgumentList.Arguments.Select(a => a.ToString()).ToList(), LineOf(invocation), method is not null), invocation)
                {
                    SyntaxStart = invocation.SpanStart, Method = method, Parent = parent,
                    ArgumentExpressions = implicitCreation.ArgumentList.Arguments.Select(a => ExtractExpression(a.Expression)).ToList(),
                    Expression = invocation.ToString()
                });
            }
            else
            {
                ExtractExpressionStatement(invocation, extracted, LineOf(invocation), method is not null, method, parent);
            }

            CSharpStatementInfo call = (CSharpStatementInfo)extracted.Single();
            statements.Add(call);
            bySpan[SpanOf(invocation)] = call;
            if (parent is not null)
            {
                parent.MutableChildren.Add(call);
            }
            else
            {
                method?.Statements.Add(call);
            }
        }
    }

    private static (string? TypeName, string? MemberName, List<string> Arguments) ExtractInvocationParts(ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax invocation)
        {
            string? typeName = null;
            string? memberName = null;
            if (invocation.Expression is MemberAccessExpressionSyntax ma)
            {
                typeName = ma.Expression.ToString();
                memberName = ma.Name.Identifier.Text;
            }
            else if (invocation.Expression is SimpleNameSyntax id)
            {
                memberName = id.Identifier.Text;
            }
            List<string> args = invocation.ArgumentList.Arguments
                .Select(a => a.ToString()).ToList();
            return (typeName, memberName, args);
        }
        return (null, null, []);
    }

    private static int LineOf(SyntaxNode node) =>
        node.SyntaxTree.GetText().Lines.GetLinePosition(node.SpanStart).Line + 1;

    private static int ColumnOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
    private static string? NamespaceOf(SyntaxNode node)
    {
        string[] names = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).ToArray();
        return names.Length == 0 ? null : string.Join(".", names);
    }
    private static int EndLineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
    private static int EndColumnOf(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Character + 1;

    private static int LineOf(SyntaxNodeOrToken node) =>
        node.SyntaxTree!.GetText().Lines.GetLinePosition(node.SpanStart).Line + 1;

    private static bool HasDocComment(SyntaxNode node) =>
        node.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static string? GetDocComment(SyntaxNode node)
    {
        SyntaxTrivia trivia = node.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        if (trivia == default)
        {
            return null;
        }

        string xml = trivia.ToString();
        // Strip leading /// and whitespace from each line
        IEnumerable<string> lines = xml.Split('\n')
            .Select(l => l.TrimStart().TrimStart('/').TrimStart())
            .Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join(" ", lines).Trim();
    }

    private static string ExtractBaseTypeName(TypeSyntax type) =>
        type is GenericNameSyntax generic ? generic.Identifier.Text : type.ToString();

    private static (List<RegionInfo> Regions, HashSet<int> CommentLines, List<CommentInfo> Comments) ExtractTrivia(
        CompilationUnitSyntax root, string sourceText, List<StatementInfo> statements)
    {
        List<RegionInfo> regions = new List<RegionInfo>();
        HashSet<int> commentLines = new HashSet<int>();
        List<CommentInfo> comments = new List<CommentInfo>();
        Stack<(string Name, int Line)> stack = new Stack<(string Name, int Line)>();
        string[]? lines = null;

        foreach (SyntaxTrivia trivia in root.DescendantTrivia())
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                    comments.Add(new CommentInfo("singleLine", trivia.ToString()[2..],
                        trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    commentLines.Add(trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    break;
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                    commentLines.Add(trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    break;
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    FileLinePositionSpan span = trivia.GetLocation().GetLineSpan();
                    for (int line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
                    {
                        commentLines.Add(line + 1);
                    }

                    break;
            }

            if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia))
            {
                RegionDirectiveTriviaSyntax directive = (RegionDirectiveTriviaSyntax)trivia.GetStructure()!;
                int line = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                string name = directive.EndOfDirectiveToken.LeadingTrivia.ToString().Trim();
                stack.Push((name, line));
            }
            else if (trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia) && stack.Count > 0)
            {
                EndRegionDirectiveTriviaSyntax directive = (EndRegionDirectiveTriviaSyntax)trivia.GetStructure()!;
                int endLine = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                (string? name, int startLine) = stack.Pop();

                // Extract content between region and endregion (exclusive of markers)
                lines ??= sourceText.Split('\n');
                List<string> contentLines = new List<string>();
                for (int i = startLine; i < endLine - 1 && i < lines.Length; i++)
                {
                    contentLines.Add(lines[i].TrimEnd('\r'));
                }

                string content = string.Join('\n', contentLines);

                regions.Add(new RegionInfo(name, startLine, endLine, content));
            }
            else if (trivia.IsKind(SyntaxKind.NullableDirectiveTrivia)
                && trivia.GetStructure() is NullableDirectiveTriviaSyntax { IsActive: true } nullable)
            {
                List<string> keywords = new List<string> { "nullable", nullable.SettingToken.ValueText };
                if (!nullable.TargetToken.IsKind(SyntaxKind.None))
                {
                    keywords.Add(nullable.TargetToken.ValueText);
                }

                statements.Add(new CSharpStatementInfo(new StatementInfo("directive", keywords, null, null, [],
                    LineOf(nullable), false)));
            }
        }

        return (regions, commentLines, comments);
    }
}
