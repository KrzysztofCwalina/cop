using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Cop.Providers.SourceModel;
using CheckTypeKind = Cop.Providers.SourceModel.TypeKind;

namespace Cop.Providers.SourceParsers;

public class CSharpSourceParser : ISourceParser
{
    public IReadOnlyList<string> Extensions => [".cs"];
    public string Language => "csharp";

    public SourceFile? Parse(string filePath, string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        var types = new List<TypeDeclaration>();

        // Extract using directives
        var usings = root.Usings
            .Select(u => u.Name?.ToString() ?? u.ToString().TrimEnd(';').Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();

        // Extract namespace
        string? ns = null;
        var nsDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsDecl != null) ns = nsDecl.Name.ToString();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
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
                allStatements.Add(new StatementInfo("attribute", [], attrName, null, args, LineOf(attrList), false));
            }
        }

        return new SourceFile(filePath, "csharp", types, allStatements, sourceText)
        {
            Usings = usings,
            Namespace = ns,
            Regions = ExtractRegions(root, sourceText)
        };
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
        }

        return new TypeDeclaration(
            syntax.Identifier.Text, kind, modifiers, baseTypes, decorators,
            constructors, methods, nestedTypes, [], LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax),
            Fields = fields,
            Properties = properties,
            Events = events
        };
    }

    private static TypeDeclaration ExtractEnum(EnumDeclarationSyntax syntax) =>
        new(syntax.Identifier.Text, CheckTypeKind.Enum, ExtractModifiers(syntax.Modifiers),
            syntax.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [],
            [], [], [], [],
            syntax.Members.Select(m => m.Identifier.Text).ToList(),
            LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax)
        };

    private static MethodDeclaration ExtractConstructor(ConstructorDeclarationSyntax syntax)
    {
        var method = new MethodDeclaration(".ctor", ExtractModifiers(syntax.Modifiers), [],
            null, syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax)
        };
        var methodStatements = new List<StatementInfo>();
        if (syntax.Body != null)
            ExtractStatements(syntax.Body, methodStatements, isInMethod: true, method: method, parent: null);
        if (syntax.ExpressionBody != null)
            ExtractExpressionStatement(syntax.ExpressionBody.Expression, methodStatements,
                LineOf(syntax.ExpressionBody), isInMethod: true, method: method, parent: null);
        method.Statements = methodStatements;
        return method;
    }

    private static MethodDeclaration ExtractMethod(MethodDeclarationSyntax syntax)
    {
        var method = new MethodDeclaration(syntax.Identifier.Text, ExtractModifiers(syntax.Modifiers),
            syntax.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList(),
            ExtractTypeReference(syntax.ReturnType),
            syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax))
        {
            HasDocComment = HasDocComment(syntax)
        };
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
            HasDocComment = HasDocComment(syntax)
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
            DefaultValueText = syntax.Default?.Value.ToString()
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
        int line = LineOf(stmt);
        switch (stmt)
        {
            case LocalDeclarationStatementSyntax decl:
            {
                List<string> keywords = [];
                string typeName = decl.Declaration.Type.ToString();
                if (decl.Declaration.Type.IsVar) keywords.Add("var");
                if (typeName == "dynamic") keywords.Add("dynamic");
                foreach (var v in decl.Declaration.Variables)
                {
                    var declStmt = new StatementInfo("declaration", keywords, typeName, v.Identifier.Text, [], line, isInMethod)
                    {
                        Method = method, Parent = parent,
                        Expression = v.Initializer?.Value.ToString()
                    };
                    results.Add(declStmt);
                    parent?._children.Add(declStmt);
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
                var throwInfo = new StatementInfo("throw", [], typeName, null, [], line, isInMethod)
                    { Method = method, Parent = parent };
                results.Add(throwInfo);
                parent?._children.Add(throwInfo);
                break;
            }
            case ReturnStatementSyntax returnStmt:
            {
                var retInfo = new StatementInfo("return", [], null, null, [], line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Expression = returnStmt.Expression?.ToString()
                };
                results.Add(retInfo);
                parent?._children.Add(retInfo);
                if (returnStmt.Expression != null)
                    ExtractExpressionStatement(returnStmt.Expression, results, line, isInMethod, method, parent);
                break;
            }
            case UsingStatementSyntax usingStmt:
            {
                var usingInfo = new StatementInfo("using", [], null, null, [], line, isInMethod)
                    { Method = method, Parent = parent };
                results.Add(usingInfo);
                parent?._children.Add(usingInfo);
                if (usingStmt.Statement != null)
                    ExtractStatementBody(usingStmt.Statement, usingInfo._children, isInMethod, method, usingInfo);
                break;
            }
            case ForEachStatementSyntax forEach:
            {
                var feInfo = new StatementInfo("foreach", [], forEach.Type.ToString(),
                    forEach.Identifier.Text, [], line, isInMethod)
                    { Method = method, Parent = parent, Condition = forEach.Expression.ToString() };
                results.Add(feInfo);
                parent?._children.Add(feInfo);
                ExtractStatementBody(forEach.Statement, feInfo._children, isInMethod, method, feInfo);
                break;
            }
            case TryStatementSyntax tryStmt:
            {
                var tryInfo = new StatementInfo("try", [], null, null, [], line, isInMethod)
                    { Method = method, Parent = parent };
                results.Add(tryInfo);
                parent?._children.Add(tryInfo);
                if (tryStmt.Block != null)
                    ExtractStatements(tryStmt.Block, tryInfo._children, isInMethod, method, tryInfo);
                foreach (var c in tryStmt.Catches)
                {
                    string? caughtType = c.Declaration?.Type.ToString();
                    bool hasRethrow = c.Block != null && c.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any();
                    bool isGeneric = caughtType is null or "Exception" or "System.Exception";
                    var catchInfo = new StatementInfo("catch", [], caughtType, null, [], LineOf(c), isInMethod)
                    {
                        HasRethrow = hasRethrow, IsErrorHandler = true, IsGenericErrorHandler = isGeneric,
                        Method = method, Parent = parent
                    };
                    results.Add(catchInfo);
                    parent?._children.Add(catchInfo);
                    if (c.Block != null)
                        ExtractStatements(c.Block, catchInfo._children, isInMethod, method, catchInfo);
                }
                break;
            }
            case IfStatementSyntax ifStmt:
            {
                var ifInfo = new StatementInfo("if", [], null, null, [], line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Condition = ifStmt.Condition.ToString()
                };
                results.Add(ifInfo);
                parent?._children.Add(ifInfo);
                ExtractStatementBody(ifStmt.Statement, ifInfo._children, isInMethod, method, ifInfo);
                if (ifStmt.Else != null)
                {
                    // else is a child of the if-statement so that statements inside else
                    // have the if (with its Condition) as an ancestor — enables guard checks
                    var elseInfo = new StatementInfo("else", [], null, null, [], LineOf(ifStmt.Else), isInMethod)
                        { Method = method, Parent = ifInfo };
                    results.Add(elseInfo);
                    ifInfo._children.Add(elseInfo);
                    ExtractStatementBody(ifStmt.Else.Statement, elseInfo._children, isInMethod, method, elseInfo);
                }
                break;
            }
            case WhileStatementSyntax ws:
            {
                var whileInfo = new StatementInfo("while", [], null, null, [], line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Condition = ws.Condition.ToString()
                };
                results.Add(whileInfo);
                parent?._children.Add(whileInfo);
                ExtractStatementBody(ws.Statement, whileInfo._children, isInMethod, method, whileInfo);
                break;
            }
            case ForStatementSyntax fs:
            {
                var forInfo = new StatementInfo("for", [], null, null, [], line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Condition = fs.Condition?.ToString()
                };
                results.Add(forInfo);
                parent?._children.Add(forInfo);
                ExtractStatementBody(fs.Statement, forInfo._children, isInMethod, method, forInfo);
                break;
            }
            case SwitchStatementSyntax sw:
            {
                var switchInfo = new StatementInfo("switch", [], null, null, [], line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Expression = sw.Expression.ToString()
                };
                results.Add(switchInfo);
                parent?._children.Add(switchInfo);
                foreach (var section in sw.Sections)
                    foreach (var sectionStmt in section.Statements)
                        ExtractStatement(sectionStmt, switchInfo._children, isInMethod, method, switchInfo);
                break;
            }
            case BlockSyntax nested:
                ExtractStatements(nested, results, isInMethod, method, parent);
                break;
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
                else if (invocation.Expression is IdentifierNameSyntax id)
                {
                    memberName = id.Identifier.Text;
                }
                var args = invocation.ArgumentList.Arguments
                    .Select(a => a.ToString()).ToList();
                var callInfo = new StatementInfo("call", [], typeName, memberName, args, line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Expression = invocation.ToString()
                };
                results.Add(callInfo);
                parent?._children.Add(callInfo);
                break;
            }
            case AwaitExpressionSyntax awaitExpr:
            {
                var (awaitType, awaitMember, awaitArgs) = ExtractInvocationParts(awaitExpr.Expression);
                var awaitInfo = new StatementInfo("await", [], awaitType, awaitMember, awaitArgs, line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Expression = awaitExpr.Expression.ToString()
                };
                results.Add(awaitInfo);
                parent?._children.Add(awaitInfo);
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
                var ctorInfo = new StatementInfo("call", [], ctorType, null, args, line, isInMethod)
                {
                    Method = method, Parent = parent,
                    Expression = objCreate.ToString()
                };
                results.Add(ctorInfo);
                parent?._children.Add(ctorInfo);
                break;
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
            else if (invocation.Expression is IdentifierNameSyntax id)
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
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int LineOf(SyntaxNodeOrToken node) =>
        node.GetLocation()!.GetLineSpan().StartLinePosition.Line + 1;

    private static bool HasDocComment(SyntaxNode node) =>
        node.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static string ExtractBaseTypeName(TypeSyntax type) =>
        type is GenericNameSyntax generic ? generic.Identifier.Text : type.ToString();

    private static List<RegionInfo> ExtractRegions(CompilationUnitSyntax root, string sourceText)
    {
        var regions = new List<RegionInfo>();
        var stack = new Stack<(string Name, int Line)>();
        var lines = sourceText.Split('\n');

        foreach (var trivia in root.DescendantTrivia())
        {
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
                var contentLines = new List<string>();
                for (int i = startLine; i < endLine - 1 && i < lines.Length; i++)
                    contentLines.Add(lines[i].TrimEnd('\r'));
                var content = string.Join('\n', contentLines);

                regions.Add(new RegionInfo(name, startLine, endLine, content));
            }
        }

        return regions;
    }
}
