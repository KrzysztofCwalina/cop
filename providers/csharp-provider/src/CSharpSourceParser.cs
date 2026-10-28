using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Cop.Providers.SourceModel;
using CheckTypeKind = Cop.Providers.SourceModel.TypeKind;

namespace Cop.Providers.SourceParsers;

public class CSharpSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".cs"];
    public override string Language => "csharp";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        return ParseWithSemantics(filePath, sourceText, tree, model: null);
    }

    /// <summary>
    /// Parses a C# file using a pre-built syntax tree and optional semantic model.
    /// When a semantic model is provided, type resolution uses Roslyn's semantic APIs
    /// instead of string-based heuristics.
    /// </summary>
    public SourceFile? ParseWithSemantics(string filePath, string sourceText, SyntaxTree tree, SemanticModel? model)
    {
        var root = tree.GetCompilationUnitRoot();

        var types = new List<TypeDeclaration>();

        // Extract using directives
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString() ?? u.ToString().TrimEnd(';').Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();

        // Single-pass: extract namespace and top-level types in one traversal
        string? ns = null;
        foreach (var node in root.DescendantNodes(node =>
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
                        types.Add(ExtractType((TypeDeclarationSyntax)node));
                    break;
                case EnumDeclarationSyntax enumDecl:
                    if (enumDecl.Parent is not TypeDeclarationSyntax)
                        types.Add(ExtractEnum(enumDecl));
                    break;
            }
        }

        // Collect all statements from methods (already built as trees during type extraction)
        // plus global and assembly-level statements
        var allStatements = new List<StatementInfo>();

        // Flatten method statements from all types (already constructed)
        foreach (var type in types)
            FlattenTypeStatements(type, allStatements);

        // Extract top-level (global) statements
        foreach (var globalStmt in root.Members.OfType<GlobalStatementSyntax>())
        {
            ExtractStatement(globalStmt.Statement, allStatements, isInMethod: false, method: null, parent: null);
        }

        // Extract assembly-level attributes as statements
        foreach (var attrList in root.AttributeLists)
        {
            if (attrList.Target?.Identifier.Text != "assembly") continue;
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString().Replace("Attribute", "");
                var args = attr.ArgumentList?.Arguments
                    .Select(a => a.Expression.ToString().Trim('"')).ToList() ?? [];
                allStatements.Add(new CSharpStatementInfo("attribute", [], attrName, null, args, LineOf(attrList), false));
            }
        }

        AddNestedCalls(root, types, allStatements);
        var (regions, commentLines) = ExtractTrivia(root, sourceText, allStatements);
        allStatements = allStatements.OrderBy(s => s.Line)
            .ThenBy(s => (s as CSharpStatementInfo)?.SyntaxStart ?? -1).ToList();

        // Enrich with semantic information when model is available
        if (model != null)
        {
            try { EnrichWithSemantics(tree, model, types, allStatements); }
            catch { /* graceful degradation — syntax data still valid */ }
        }

        return new SourceFile(filePath, "csharp", types, allStatements, sourceText)
        {
            Usings = usings,
            Imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => new ImportInfo(u.Name?.ToString() ?? "", LineOf(u))
            {
                Alias = u.Alias?.Name.Identifier.ValueText,
                IsStatic = u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword),
                Column = ColumnOf(u), EndLine = EndLineOf(u), EndColumn = EndColumnOf(u)
            }).ToList(),
            Namespace = ns,
            Regions = regions,
            CommentLines = commentLines,
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
        var errors = new List<string>();
        foreach (var diag in tree.GetDiagnostics())
        {
            if (diag.Severity != DiagnosticSeverity.Error) continue;
            var pos = diag.Location.GetLineSpan().StartLinePosition;
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

    public static void EnrichTypeNames(SyntaxTree tree, SemanticModel model, List<StatementInfo> statements)
        => EnrichWithSemantics(tree, model, [], statements, typeNamesOnly: true);

    public static void EnrichTypeNames(SyntaxTree tree, SemanticModel invocationModel,
        SemanticModel inferenceModel, List<StatementInfo> statements)
        => EnrichWithSemantics(tree, invocationModel, [], statements, typeNamesOnly: true, inferenceModel);

    public static void DeferTypeNames(SyntaxTree tree, List<StatementInfo> statements,
        Func<SemanticModel> invocationModel, Func<SemanticModel> inferenceModel)
    {
        var invocations = new Lazy<bool>(() =>
        {
            EnrichWithSemantics(tree, invocationModel(), [], statements, typeNamesOnly: true,
                resolveInferences: false);
            return true;
        });
        var inferences = new Lazy<bool>(() =>
        {
            EnrichWithSemantics(tree, inferenceModel(), [], statements, typeNamesOnly: true,
                resolveInvocations: false);
            return true;
        });
        foreach (var statement in statements)
        {
            statement.DeferTypeNameAnalysis(statement is CSharpStatementInfo { RequiresTypeNameAnalysis: true }
                ? statement.Kind == "declaration" ? inferences : invocations
                : null);
        }
    }

    private static void EnrichWithSemantics(SyntaxTree tree, SemanticModel model,
        List<TypeDeclaration> types, List<StatementInfo> allStatements, bool typeNamesOnly,
        SemanticModel? inferenceModel = null, bool resolveInvocations = true, bool resolveInferences = true)
    {
        var root = tree.GetCompilationUnitRoot();

        var typesByStart = new Dictionary<int, TypeDeclaration>();
        void AddTypes(IEnumerable<TypeDeclaration> declarations)
        {
            foreach (var type in declarations)
            {
                if (type is CSharpTypeDeclaration cs) typesByStart[cs.SyntaxStart] = type;
                AddTypes(type.NestedTypes);
            }
        }
        AddTypes(types);

        // Enrich type declarations with interface information
        foreach (var node in typeNamesOnly ? Enumerable.Empty<TypeDeclarationSyntax>()
                     : root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (typesByStart.TryGetValue(node.SpanStart, out var typeDecl))
            {
                try
                {
                    var symbol = model.GetDeclaredSymbol(node);
                    if (symbol is INamedTypeSymbol namedType)
                    {
                        typeDecl.Interfaces = namedType.AllInterfaces
                            .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                            .ToList();
                    }
                }
                catch { /* graceful degradation if semantic resolution fails */ }
            }
        }

        // Enrich object-creation statements with constructed type interfaces
        var stmtsByLine = new Dictionary<int, List<StatementInfo>>();
        foreach (var s in allStatements)
        {
            if (!stmtsByLine.TryGetValue(s.Line, out var list))
            {
                list = [];
                stmtsByLine[s.Line] = list;
            }
            list.Add(s);
        }

        foreach (var node in root.DescendantNodes())
        {
            if (node is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            {
                if (typeNamesOnly) continue;
                var line = LineOf(node);
                if (stmtsByLine.TryGetValue(line, out var stmts))
                {
                    try
                    {
                        var typeInfo = model.GetTypeInfo(node);
                        if (typeInfo.Type is INamedTypeSymbol createdType)
                        {
                            var interfaces = createdType.AllInterfaces
                                .Select(i => i.Name)
                                .ToList();

                            // Match to the statement that has a "new" kind or contains this type
                            foreach (var stmt in stmts)
                            {
                                if (stmt is CSharpStatementInfo cs && cs.SyntaxStart == node.SpanStart
                                    && (stmt.Kind == "new" || stmt.Kind == "call"))
                                {
                                    stmt.ConstructedTypeInterfaces = interfaces;
                                    if (node is ImplicitObjectCreationExpressionSyntax)
                                        stmt.TypeName = createdType.Name;
                                    break;
                                }
                            }
                        }
                    }
                    catch { /* graceful degradation */ }
                }
            }
            // Resolve method call type names from semantic model
            else if (resolveInvocations && node is InvocationExpressionSyntax invocation)
            {
                var line = LineOf(node);
                if (stmtsByLine.TryGetValue(line, out var stmts))
                {
                    try
                    {
                        var symbolInfo = model.GetSymbolInfo(invocation);
                        if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
                        {
                            var containingType = methodSymbol.ContainingType?.Name;
                            if (containingType != null)
                            {
                                foreach (var stmt in stmts)
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
                    catch { }
                }
            }
            // Resolve the inferred type for `var` declarations, so `var` is never a TypeName.
            // The `var` keyword stays in Keywords; TypeName becomes the actual inferred type.
            else if (resolveInferences && node is LocalDeclarationStatementSyntax varDecl && varDecl.Declaration.Type.IsVar)
            {
                var line = LineOf(node);
                if (stmtsByLine.TryGetValue(line, out var stmts))
                {
                    try
                    {
                        var inferred = (inferenceModel ?? model).GetTypeInfo(varDecl.Declaration.Type).Type
                            ?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                            ?.TrimEnd('?');
                        if (!string.IsNullOrEmpty(inferred) && inferred != "?" && inferred != "var")
                        {
                            // Match by variable name so multiple declarations on one line stay distinct.
                            var starts = varDecl.Declaration.Variables.Select(v => v.SpanStart).ToHashSet();
                            foreach (var stmt in stmts)
                                if (stmt is CSharpStatementInfo cs && stmt.Kind == "declaration" && starts.Contains(cs.SyntaxStart))
                                    stmt.TypeName = inferred;
                        }
                    }
                    catch { }
                }
            }
        }
    }

    private static void FlattenTypeStatements(TypeDeclaration type, List<StatementInfo> allStatements)
    {
        foreach (var ctor in type.Constructors)
            FlattenStatements(ctor.Statements, allStatements);
        foreach (var method in type.Methods)
            FlattenStatements(method.Statements, allStatements);
        foreach (var nested in type.NestedTypes)
            FlattenTypeStatements(nested, allStatements);
    }

    private static void FlattenStatements(List<StatementInfo> statements, List<StatementInfo> target)
    {
        foreach (var stmt in statements)
        {
            target.Add(stmt);
            if (stmt._children.Count > 0)
                FlattenStatements(stmt._children, target);
        }
    }

    private static TypeDeclaration ExtractType(TypeDeclarationSyntax syntax)
    {
        var kind = syntax switch
        {
            ClassDeclarationSyntax => CheckTypeKind.Class,
            StructDeclarationSyntax => CheckTypeKind.Struct,
            InterfaceDeclarationSyntax => CheckTypeKind.Interface,
            RecordDeclarationSyntax r =>
                r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? CheckTypeKind.Struct : CheckTypeKind.Class,
            _ => CheckTypeKind.Class
        };

        var modifiers = ExtractModifiers(syntax.Modifiers);
        var baseTypes = syntax.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [];
        var decorators = syntax.AttributeLists
            .SelectMany(a => a.Attributes)
            .Select(a => a.Name.ToString()).ToList();

        var constructors = new List<MethodDeclaration>();
        var methods = new List<MethodDeclaration>();
        var nestedTypes = new List<TypeDeclaration>();
        var fields = new List<FieldDeclaration>();
        var properties = new List<PropertyDeclaration>();
        var events = new List<EventDeclaration>();

        foreach (var member in syntax.Members)
        {
            if (member is ConstructorDeclarationSyntax ctor)
                constructors.Add(ExtractConstructor(ctor));
            else if (member is MethodDeclarationSyntax method)
                methods.Add(ExtractMethod(method));
            else if (member is FieldDeclarationSyntax field)
                fields.AddRange(ExtractFields(field));
            else if (member is PropertyDeclarationSyntax prop)
                properties.Add(ExtractProperty(prop));
            else if (member is EventFieldDeclarationSyntax eventField)
                events.AddRange(ExtractEventFields(eventField));
            else if (member is EventDeclarationSyntax eventDecl)
                events.Add(ExtractEvent(eventDecl));
            else if (member is TypeDeclarationSyntax nestedType)
                nestedTypes.Add(ExtractType(nestedType));
            else if (member is EnumDeclarationSyntax nestedEnum)
                nestedTypes.Add(ExtractEnum(nestedEnum));

            if (member is not BaseTypeDeclarationSyntax)
            {
                foreach (var callable in member.DescendantNodesAndSelf()
                             .Where(n => n is AccessorDeclarationSyntax or LocalFunctionStatementSyntax
                                 or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax))
                    methods.Add(ExtractCallable(callable));
                if (member is PropertyDeclarationSyntax { ExpressionBody: { } body } property)
                    methods.Add(ExtractCallable(property, "get_" + property.Identifier.ValueText, body.Expression));
                if (member is IndexerDeclarationSyntax { ExpressionBody: { } indexerBody } indexer)
                    methods.Add(ExtractCallable(indexer, "get_Item", indexerBody.Expression));
            }
        }

        var result = new TypeDeclaration(
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
        }.AsCSharp(
            isRecord: syntax is RecordDeclarationSyntax,
            isRecordStruct: syntax is RecordDeclarationSyntax rds && rds.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword),
            isReadOnly: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)),
            isRef: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)),
            isFileLocal: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)),
            isPartial: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
            hasPrimaryConstructor: syntax.ParameterList != null,
            isGeneric: syntax.TypeParameterList != null);
        result.SyntaxStart = syntax.SpanStart;
        return result;
    }

    private static TypeDeclaration ExtractEnum(EnumDeclarationSyntax syntax) =>
        new TypeDeclaration(syntax.Identifier.Text, CheckTypeKind.Enum, ExtractModifiers(syntax.Modifiers),
            syntax.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [],
            [], [], [], [],
            syntax.Members.Select(m => m.Identifier.Text).ToList(),
            LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            Namespace = NamespaceOf(syntax),
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        }.AsCSharp(isFileLocal: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)));

    private static MethodDeclaration ExtractConstructor(ConstructorDeclarationSyntax syntax)
    {
        var method = new MethodDeclaration(".ctor", ExtractModifiers(syntax.Modifiers), [],
            null, syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        }.AsCSharp(isExpressionBodied: syntax.ExpressionBody != null);
        method.SyntaxStart = syntax.SpanStart;
        var methodStatements = new List<StatementInfo>();
        if (syntax.Body != null)
            ExtractStatements(syntax.Body, methodStatements, isInMethod: true, method: method, parent: null);
        if (syntax.ExpressionBody != null)
            ExtractExpressionStatement(syntax.ExpressionBody.Expression, methodStatements,
                LineOf(syntax.ExpressionBody), isInMethod: true, method: method, parent: null);
        method.Statements = methodStatements;
        return method;
    }

    private static CSharpMethodDeclaration ExtractCallable(SyntaxNode syntax, string? name = null, ExpressionSyntax? expression = null)
    {
        var (callableName, parameters, returnType, body, arrow) = syntax switch
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
        var modifiers = syntax switch
        {
            LocalFunctionStatementSyntax localFunction => localFunction.Modifiers,
            AccessorDeclarationSyntax accessor => accessor.Modifiers,
            BaseMethodDeclarationSyntax callable => callable.Modifiers,
            _ => default
        };
        var result = new MethodDeclaration(callableName, ExtractModifiers(modifiers), [],
            returnType is null ? null : ExtractTypeReference(returnType),
            parameters?.Parameters.Select(ExtractParameter).ToList() ?? [], LineOf(syntax))
        {
            TypeParameters = syntax is LocalFunctionStatementSyntax genericLocal
                ? genericLocal.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [] : [],
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        }
            .AsCSharp(isExpressionBodied: arrow is not null || expression is not null,
                isGeneric: syntax is LocalFunctionStatementSyntax { TypeParameterList: not null });
        result.SyntaxStart = syntax.SpanStart;
        if (body is not null) ExtractStatements(body, result.Statements, true, result);
        if ((expression ?? arrow?.Expression) is { } value)
            ExtractExpressionStatement(value, result.Statements, LineOf(value), true, result);
        return result;
    }

    private static MethodDeclaration ExtractMethod(MethodDeclarationSyntax syntax)
    {
        var method = new MethodDeclaration(syntax.Identifier.Text, ExtractModifiers(syntax.Modifiers),
            syntax.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList(),
            ExtractTypeReference(syntax.ReturnType),
            syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax),
            DocComment = GetDocComment(syntax),
            TypeParameters = syntax.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [],
            Column = ColumnOf(syntax), EndLine = EndLineOf(syntax), EndColumn = EndColumnOf(syntax)
        }.AsCSharp(
            isExtension: syntax.ParameterList.Parameters.FirstOrDefault()?.Modifiers
                .Any(m => m.IsKind(SyntaxKind.ThisKeyword)) == true,
            isPartial: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
            isUnsafe: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)),
            isExtern: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)),
            isExpressionBodied: syntax.ExpressionBody != null,
            isGeneric: syntax.TypeParameterList != null);
        method.SyntaxStart = syntax.SpanStart;
        var methodStatements = new List<StatementInfo>();
        if (syntax.Body != null)
            ExtractStatements(syntax.Body, methodStatements, isInMethod: true, method: method, parent: null);
        if (syntax.ExpressionBody != null)
            ExtractExpressionStatement(syntax.ExpressionBody.Expression, methodStatements,
                LineOf(syntax.ExpressionBody), isInMethod: true, method: method, parent: null);
        method.Statements = methodStatements;
        return method;
    }

    private static List<FieldDeclaration> ExtractFields(FieldDeclarationSyntax syntax)
    {
        var modifiers = ExtractModifiers(syntax.Modifiers);
        var type = ExtractTypeReference(syntax.Declaration.Type);
        return syntax.Declaration.Variables.Select(v =>
            new FieldDeclaration(v.Identifier.Text, type, modifiers, LineOf(v))
        ).ToList();
    }

    private static PropertyDeclaration ExtractProperty(PropertyDeclarationSyntax syntax)
    {
        var hasGetter = syntax.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? syntax.ExpressionBody != null;
        var hasSetter = syntax.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) ?? false;
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
        var modifiers = ExtractModifiers(syntax.Modifiers);
        var type = syntax.Declaration.Type is not null ? ExtractTypeReference(syntax.Declaration.Type) : null;
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
                var inner = ExtractTypeReference(nullable.ElementType);
                return new TypeReference(inner.Name, inner.Namespace,
                    inner.GenericArguments, originalText);
            case ArrayTypeSyntax array:
                var elemRef = ExtractTypeReference(array.ElementType);
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
            return generic.TypeArgumentList.Arguments.Select(ExtractTypeReference).ToList();
        return [];
    }

    private static Modifier ExtractModifiers(SyntaxTokenList modifiers)
    {
        var result = Modifier.None;
        foreach (var mod in modifiers)
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
        foreach (var stmt in block.Statements)
            ExtractStatement(stmt, results, isInMethod, method, parent);
    }

    /// <summary>
    /// Extract a statement body — handles both block and single-statement (non-block) bodies.
    /// </summary>
    private static void ExtractStatementBody(StatementSyntax body, List<StatementInfo> results,
        bool isInMethod, MethodDeclaration? method, StatementInfo? parent)
    {
        if (body is BlockSyntax block)
            ExtractStatements(block, results, isInMethod, method, parent);
        else
            ExtractStatement(body, results, isInMethod, method, parent);
    }

    private static void ExtractStatement(StatementSyntax stmt, List<StatementInfo> results,
        bool isInMethod = true, MethodDeclaration? method = null, StatementInfo? parent = null)
    {
        int first = results.Count;
        int line = LineOf(stmt);
        switch (stmt)
        {
            case LocalDeclarationStatementSyntax decl:
            {
                List<string> keywords = [];
                // `var` is a keyword, not a type name — record it in Keywords and leave
                // TypeName empty here; the inferred type is filled in by EnrichWithSemantics.
                bool isVar = decl.Declaration.Type.IsVar;
                string typeName = isVar ? "" : decl.Declaration.Type.ToString();
                if (isVar) keywords.Add("var");
                if (typeName == "dynamic") keywords.Add("dynamic");
                foreach (var v in decl.Declaration.Variables)
                {
                    var declStmt = new CSharpStatementInfo("declaration", keywords, typeName, v.Identifier.Text, [], line, isInMethod, v)
                    {
                        Method = method, Parent = parent,
                        RequiresTypeNameAnalysis = isVar,
                        Expression = v.Initializer?.Value.ToString(),
                        ValueExpression = v.Initializer is null ? null : ExtractExpression(v.Initializer.Value)
                    };
                    results.Add(declStmt);
                    // Also extract expressions from initializers (e.g., await, invocations)
                    if (v.Initializer?.Value != null)
                        ExtractExpressionStatement(v.Initializer.Value, results, line, isInMethod, method, parent);
                }
                break;
            }
            case ExpressionStatementSyntax exprStmt:
                ExtractExpressionStatement(exprStmt.Expression, results, line, isInMethod, method, parent);
                break;
            case ThrowStatementSyntax throwStmt:
            {
                string? typeName = throwStmt.Expression is ObjectCreationExpressionSyntax creation
                    ? creation.Type.ToString() : null;
                var throwInfo = new CSharpStatementInfo("throw", [], typeName, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(throwInfo);
                break;
            }
            case ReturnStatementSyntax returnStmt:
            {
                var retInfo = new CSharpStatementInfo("return", [], null, null, [], line, isInMethod, stmt)
                {
                    Method = method, Parent = parent,
                    Expression = returnStmt.Expression?.ToString()
                };
                results.Add(retInfo);
                if (returnStmt.Expression != null)
                    ExtractExpressionStatement(returnStmt.Expression, results, line, isInMethod, method, parent);
                break;
            }
            case UsingStatementSyntax usingStmt:
            {
                var usingInfo = new CSharpStatementInfo("using", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(usingInfo);
                if (usingStmt.Statement != null)
                    ExtractStatementBody(usingStmt.Statement, usingInfo._children, isInMethod, method, usingInfo);
                break;
            }
            case ForEachStatementSyntax forEach:
            {
                var feInfo = new CSharpStatementInfo("foreach", [], forEach.Type.ToString(),
                    forEach.Identifier.Text, [], line, isInMethod, stmt)
                {
                    Method = method, Parent = parent,
                    Condition = forEach.Expression.ToString(),
                    IsBraced = forEach.Statement is BlockSyntax,
                    IsAwaitForeach = forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                };
                results.Add(feInfo);
                ExtractStatementBody(forEach.Statement, feInfo._children, isInMethod, method, feInfo);
                break;
            }
            case TryStatementSyntax tryStmt:
            {
                var tryInfo = new CSharpStatementInfo("try", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(tryInfo);
                if (tryStmt.Block != null)
                    ExtractStatements(tryStmt.Block, tryInfo._children, isInMethod, method, tryInfo);
                foreach (var c in tryStmt.Catches)
                {
                    string? caughtType = c.Declaration?.Type.ToString();
                    bool hasRethrow = c.Block.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax
                            && n is not LocalFunctionStatementSyntax && n is not CatchClauseSyntax)
                        .OfType<ThrowStatementSyntax>().Any(t => t.Expression is null);
                    bool isGeneric = caughtType is null or "Exception" or "System.Exception";
                    var catchInfo = new CSharpStatementInfo("catch", [], caughtType, null, [], LineOf(c), isInMethod, c)
                    {
                        HasRethrow = hasRethrow, IsErrorHandler = true, IsGenericErrorHandler = isGeneric,
                        Method = method, Parent = tryInfo,
                        HasCatchFilter = c.Filter != null,
                        SyntaxStart = c.SpanStart
                    };
                    tryInfo._children.Add(catchInfo);
                    ExtractStatements(c.Block, catchInfo._children, isInMethod, method, catchInfo);
                }
                if (tryStmt.Finally is { } finallyClause)
                {
                    var finallyInfo = new CSharpStatementInfo("finally", [], null, null, [], LineOf(finallyClause), isInMethod, finallyClause)
                        { Method = method, Parent = tryInfo, SyntaxStart = finallyClause.SpanStart };
                    tryInfo._children.Add(finallyInfo);
                    ExtractStatements(finallyClause.Block, finallyInfo._children, isInMethod, method, finallyInfo);
                }
                break;
            }
            case IfStatementSyntax ifStmt:
            {
                var ifInfo = new CSharpStatementInfo("if", [], null, null, [], line, isInMethod, stmt)
                {
                    Method = method, Parent = parent,
                    Condition = ifStmt.Condition.ToString(),
                    IsBraced = ifStmt.Statement is BlockSyntax
                };
                results.Add(ifInfo);
                ExtractStatementBody(ifStmt.Statement, ifInfo._children, isInMethod, method, ifInfo);
                if (ifStmt.Else != null)
                {
                    // else is a child of the if-statement so that statements inside else
                    // have the if (with its Condition) as an ancestor — enables guard checks
                    var elseInfo = new CSharpStatementInfo("else", [], null, null, [], LineOf(ifStmt.Else), isInMethod, ifStmt.Else)
                    {
                        Method = method, Parent = ifInfo,
                        IsBraced = ifStmt.Else.Statement is BlockSyntax
                    };
                    ifInfo._children.Add(elseInfo);
                    ExtractStatementBody(ifStmt.Else.Statement, elseInfo._children, isInMethod, method, elseInfo);
                }
                break;
            }
            case WhileStatementSyntax ws:
            {
                var whileInfo = new CSharpStatementInfo("while", [], null, null, [], line, isInMethod, stmt)
                {
                    Method = method, Parent = parent,
                    Condition = ws.Condition.ToString(),
                    IsBraced = ws.Statement is BlockSyntax
                };
                results.Add(whileInfo);
                ExtractStatementBody(ws.Statement, whileInfo._children, isInMethod, method, whileInfo);
                break;
            }
            case ForStatementSyntax fs:
            {
                var forInfo = new CSharpStatementInfo("for", [], null, null, [], line, isInMethod, stmt)
                {
                    Method = method, Parent = parent,
                    Condition = fs.Condition?.ToString(),
                    IsBraced = fs.Statement is BlockSyntax
                };
                results.Add(forInfo);
                ExtractStatementBody(fs.Statement, forInfo._children, isInMethod, method, forInfo);
                break;
            }
            case SwitchStatementSyntax sw:
            {
                var switchInfo = new CSharpStatementInfo("switch", [], null, null, [], line, isInMethod, stmt)
                {
                    Method = method, Parent = parent,
                    Expression = sw.Expression.ToString()
                };
                results.Add(switchInfo);
                foreach (var section in sw.Sections)
                    foreach (var sectionStmt in section.Statements)
                        ExtractStatement(sectionStmt, switchInfo._children, isInMethod, method, switchInfo);
                break;
            }
            case BlockSyntax nested:
                ExtractStatements(nested, results, isInMethod, method, parent);
                break;
            case LockStatementSyntax lockStmt:
            {
                var info = new CSharpStatementInfo("lock", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent, Expression = lockStmt.Expression.ToString() };
                results.Add(info);
                ExtractStatementBody(lockStmt.Statement, info._children, isInMethod, method, info);
                break;
            }
            case UnsafeStatementSyntax unsafeStmt:
            {
                var info = new CSharpStatementInfo("unsafe", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                if (unsafeStmt.Block != null)
                    ExtractStatements(unsafeStmt.Block, info._children, isInMethod, method, info);
                break;
            }
            case FixedStatementSyntax fixedStmt:
            {
                var info = new CSharpStatementInfo("fixed", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                ExtractStatementBody(fixedStmt.Statement, info._children, isInMethod, method, info);
                break;
            }
            case CheckedStatementSyntax checkedStmt:
            {
                bool isUnchecked = checkedStmt.Keyword.IsKind(SyntaxKind.UncheckedKeyword);
                var info = new CSharpStatementInfo(isUnchecked ? "unchecked" : "checked", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                if (checkedStmt.Block != null)
                    ExtractStatements(checkedStmt.Block, info._children, isInMethod, method, info);
                break;
            }
            case YieldStatementSyntax yieldStmt:
            {
                var info = new CSharpStatementInfo("yield", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent, Expression = yieldStmt.Expression?.ToString() };
                results.Add(info);
                if (yieldStmt.Expression != null)
                    ExtractExpressionStatement(yieldStmt.Expression, results, line, isInMethod, method, parent);
                break;
            }
            case GotoStatementSyntax:
            {
                var info = new CSharpStatementInfo("goto", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                break;
            }
            case DoStatementSyntax loop:
            {
                var info = new CSharpStatementInfo("do", [], null, null, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent, Condition = loop.Condition.ToString(), IsBraced = loop.Statement is BlockSyntax };
                results.Add(info);
                ExtractStatementBody(loop.Statement, info._children, isInMethod, method, info);
                break;
            }
            case ForEachVariableStatementSyntax loop:
            {
                var info = new CSharpStatementInfo("foreach", [], null, null, [], line, isInMethod, stmt)
                {
                    Method = method, Parent = parent, Expression = loop.Variable.ToString(),
                    Condition = loop.Expression.ToString(), IsBraced = loop.Statement is BlockSyntax,
                    IsAwaitForeach = loop.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                };
                results.Add(info);
                ExtractStatementBody(loop.Statement, info._children, isInMethod, method, info);
                break;
            }
            case LabeledStatementSyntax label:
            {
                var info = new CSharpStatementInfo("label", [], null, label.Identifier.ValueText, [], line, isInMethod, stmt)
                    { Method = method, Parent = parent };
                results.Add(info);
                ExtractStatementBody(label.Statement, info._children, isInMethod, method, info);
                break;
            }
            case BreakStatementSyntax or ContinueStatementSyntax:
                results.Add(new CSharpStatementInfo(stmt is BreakStatementSyntax ? "break" : "continue",
                    [], null, null, [], line, isInMethod, stmt) { Method = method, Parent = parent });
                break;
            case LocalFunctionStatementSyntax local:
                results.Add(new CSharpStatementInfo("localfunction", [], local.ReturnType.ToString(),
                    local.Identifier.ValueText, [], line, isInMethod, stmt) { Method = method, Parent = parent });
                break;
        }
        for (int i = first; i < results.Count; i++)
            if (results[i] is CSharpStatementInfo { SyntaxStart: < 0 } info)
                info.SyntaxStart = stmt.SpanStart;
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
                var args = invocation.ArgumentList.Arguments
                    .Select(a => a.ToString()).ToList();
                var callInfo = new CSharpStatementInfo("call", [], typeName, memberName, args, LineOf(invocation), isInMethod, invocation)
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
                var (awaitType, awaitMember, awaitArgs) = ExtractInvocationParts(awaitExpr.Expression);
                var awaitInfo = new CSharpStatementInfo("await", [], awaitType, awaitMember, awaitArgs, line, isInMethod, awaitExpr)
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
                ExtractExpressionStatement(assignment.Right, results, line, isInMethod, method, parent);
                break;
            case ObjectCreationExpressionSyntax objCreate:
            {
                var ctorType = ExtractBaseTypeName(objCreate.Type);
                var args = objCreate.ArgumentList?.Arguments
                    .Select(a => a.ToString()).ToList() ?? [];
                var ctorInfo = new CSharpStatementInfo("call", [], ctorType, null, args, line, isInMethod, objCreate)
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
        IdentifierNameSyntax id => new("identifier") { Name = id.Identifier.ValueText },
        GenericNameSyntax name => new("identifier") { Name = name.Identifier.ValueText },
        MemberAccessExpressionSyntax member => new("memberAccess")
        {
            Name = member.Name.Identifier.ValueText, Target = ExtractExpression(member.Expression)
        },
        MemberBindingExpressionSyntax member => new("memberAccess")
        {
            Name = member.Name.Identifier.ValueText,
            Target = member.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } conditional
                ? ExtractExpression(conditional.Expression) : null
        },
        InvocationExpressionSyntax call => new("invocation")
        {
            Target = ExtractExpression(call.Expression),
            Children = call.ArgumentList.Arguments.Select(a => ExtractExpression(a.Expression)).ToList()
        },
        _ => new("other")
    };

    private static void AddNestedCalls(CompilationUnitSyntax root, List<TypeDeclaration> types, List<StatementInfo> statements)
    {
        var byStart = statements.OfType<CSharpStatementInfo>()
            .Where(s => s.SyntaxStart >= 0).GroupBy(s => s.SyntaxStart)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Kind == "call" ? 0 : 1).First());
        var methods = new Dictionary<int, MethodDeclaration>();
        void AddMethods(TypeDeclaration type)
        {
            foreach (var method in type.Methods.Concat(type.Constructors))
                if (method is CSharpMethodDeclaration cs) methods.TryAdd(cs.SyntaxStart, method);
            foreach (var nested in type.NestedTypes)
                AddMethods(nested);
        }
        foreach (var type in types) AddMethods(type);

        // Statement extraction already covers direct calls. Walk syntax to include calls in
        // conditions, field initializers, arguments, and other nested expressions exactly once.
        foreach (var invocation in root.DescendantNodes().OfType<ExpressionSyntax>()
                     .Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
        {
            if (byStart.TryGetValue(invocation.SpanStart, out var existing) && existing.Kind == "call")
                continue;
            var parent = invocation.Ancestors().Select(n => byStart.GetValueOrDefault(n.SpanStart))
                .FirstOrDefault(s => s is not null);
            var method = invocation.Ancestors().Select(n => methods.GetValueOrDefault(n.SpanStart))
                .FirstOrDefault(m => m is not null);
            var extracted = new List<StatementInfo>();
            if (invocation is ImplicitObjectCreationExpressionSyntax implicitCreation)
            {
                extracted.Add(new CSharpStatementInfo("call", [], null, null,
                    implicitCreation.ArgumentList.Arguments.Select(a => a.ToString()).ToList(), LineOf(invocation), method is not null, invocation)
                {
                    SyntaxStart = invocation.SpanStart, Method = method, Parent = parent,
                    ArgumentExpressions = implicitCreation.ArgumentList.Arguments.Select(a => ExtractExpression(a.Expression)).ToList(),
                    Expression = invocation.ToString()
                });
            }
            else ExtractExpressionStatement(invocation, extracted, LineOf(invocation), method is not null, method, parent);
            var call = (CSharpStatementInfo)extracted.Single();
            statements.Add(call);
            byStart[invocation.SpanStart] = call;
            if (parent is not null) parent._children.Add(call);
            else method?.Statements.Add(call);
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
            var args = invocation.ArgumentList.Arguments
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
        var names = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).ToArray();
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
        var trivia = node.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        if (trivia == default) return null;
        var xml = trivia.ToString();
        // Strip leading /// and whitespace from each line
        var lines = xml.Split('\n')
            .Select(l => l.TrimStart().TrimStart('/').TrimStart())
            .Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join(" ", lines).Trim();
    }

    private static string ExtractBaseTypeName(TypeSyntax type) =>
        type is GenericNameSyntax generic ? generic.Identifier.Text : type.ToString();

    private static (List<RegionInfo> Regions, HashSet<int> CommentLines) ExtractTrivia(
        CompilationUnitSyntax root, string sourceText, List<StatementInfo> statements)
    {
        var regions = new List<RegionInfo>();
        var commentLines = new HashSet<int>();
        var stack = new Stack<(string Name, int Line)>();
        string[]? lines = null;

        foreach (var trivia in root.DescendantTrivia())
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                    commentLines.Add(trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    break;
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    var span = trivia.GetLocation().GetLineSpan();
                    for (int line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
                        commentLines.Add(line + 1);
                    break;
            }

            if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia))
            {
                var directive = (RegionDirectiveTriviaSyntax)trivia.GetStructure()!;
                var line = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var name = directive.EndOfDirectiveToken.LeadingTrivia.ToString().Trim();
                stack.Push((name, line));
            }
            else if (trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia) && stack.Count > 0)
            {
                var directive = (EndRegionDirectiveTriviaSyntax)trivia.GetStructure()!;
                var endLine = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var (name, startLine) = stack.Pop();

                // Extract content between region and endregion (exclusive of markers)
                lines ??= sourceText.Split('\n');
                var contentLines = new List<string>();
                for (int i = startLine; i < endLine - 1 && i < lines.Length; i++)
                    contentLines.Add(lines[i].TrimEnd('\r'));
                var content = string.Join('\n', contentLines);

                regions.Add(new RegionInfo(name, startLine, endLine, content));
            }
            else if (trivia.IsKind(SyntaxKind.NullableDirectiveTrivia)
                && trivia.GetStructure() is NullableDirectiveTriviaSyntax { IsActive: true } nullable)
            {
                var keywords = new List<string> { "nullable", nullable.SettingToken.ValueText };
                if (!nullable.TargetToken.IsKind(SyntaxKind.None))
                    keywords.Add(nullable.TargetToken.ValueText);
                statements.Add(new CSharpStatementInfo("directive", keywords, null, null, [],
                    LineOf(nullable), false));
            }
        }

        return (regions, commentLines);
    }
}
