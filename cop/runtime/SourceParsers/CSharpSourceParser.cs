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
        var statements = new List<StatementInfo>();

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

        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (method.Body != null)
                ExtractStatements(method.Body, statements, isInMethod: true);
            if (method.ExpressionBody != null)
                ExtractExpressionStatement(method.ExpressionBody.Expression, statements,
                    LineOf(method.ExpressionBody), isInMethod: true);
        }

        // Extract top-level (global) statements
        foreach (var globalStmt in root.Members.OfType<GlobalStatementSyntax>())
        {
            ExtractStatement(globalStmt.Statement, statements, isInMethod: false);
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
                statements.Add(new StatementInfo("attribute", [], attrName, null, args, LineOf(attrList), false));
            }
        }

        return new SourceFile(filePath, "csharp", types, statements, sourceText)
        {
            Usings = usings,
            Namespace = ns
        };
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

        foreach (var member in syntax.Members)
        {
            if (member is ConstructorDeclarationSyntax ctor)
                constructors.Add(ExtractConstructor(ctor));
            else if (member is MethodDeclarationSyntax method)
                methods.Add(ExtractMethod(method));
            else if (member is TypeDeclarationSyntax nestedType)
                nestedTypes.Add(ExtractType(nestedType));
            else if (member is EnumDeclarationSyntax nestedEnum)
                nestedTypes.Add(ExtractEnum(nestedEnum));
        }

        return new TypeDeclaration(
            syntax.Identifier.Text, kind, modifiers, baseTypes, decorators,
            constructors, methods, nestedTypes, [], LineOf(syntax));
    }

    private static TypeDeclaration ExtractEnum(EnumDeclarationSyntax syntax) =>
        new(syntax.Identifier.Text, CheckTypeKind.Enum, ExtractModifiers(syntax.Modifiers),
            syntax.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [],
            [], [], [], [],
            syntax.Members.Select(m => m.Identifier.Text).ToList(),
            LineOf(syntax));

    private static MethodDeclaration ExtractConstructor(ConstructorDeclarationSyntax syntax)
    {
        var methodStatements = new List<StatementInfo>();
        if (syntax.Body != null)
            ExtractStatements(syntax.Body, methodStatements, isInMethod: true);
        if (syntax.ExpressionBody != null)
            ExtractExpressionStatement(syntax.ExpressionBody.Expression, methodStatements,
                LineOf(syntax.ExpressionBody), isInMethod: true);
        return new(".ctor", ExtractModifiers(syntax.Modifiers), [],
            null, syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax)) { Statements = methodStatements };
    }

    private static MethodDeclaration ExtractMethod(MethodDeclarationSyntax syntax)
    {
        var methodStatements = new List<StatementInfo>();
        if (syntax.Body != null)
            ExtractStatements(syntax.Body, methodStatements, isInMethod: true);
        if (syntax.ExpressionBody != null)
            ExtractExpressionStatement(syntax.ExpressionBody.Expression, methodStatements,
                LineOf(syntax.ExpressionBody), isInMethod: true);
        return new(syntax.Identifier.Text, ExtractModifiers(syntax.Modifiers),
            syntax.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList(),
            ExtractTypeReference(syntax.ReturnType),
            syntax.ParameterList.Parameters.Select(ExtractParameter).ToList(),
            LineOf(syntax)) { Statements = methodStatements };
    }

    private static ParameterDeclaration ExtractParameter(ParameterSyntax syntax) =>
        new(syntax.Identifier.Text, syntax.Type is not null ? ExtractTypeReference(syntax.Type) : null,
            syntax.Modifiers.Any(SyntaxKind.ParamsKeyword), false,
            syntax.Default is not null, LineOf(syntax));

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
                _ => Modifier.None
            };
        }
        return result;
    }

    private static void ExtractStatements(BlockSyntax block, List<StatementInfo> results, bool isInMethod = true)
    {
        foreach (var stmt in block.Statements)
            ExtractStatement(stmt, results, isInMethod);
    }

    private static void ExtractStatement(StatementSyntax stmt, List<StatementInfo> results, bool isInMethod = true)
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
                    results.Add(new StatementInfo("declaration", keywords, typeName, v.Identifier.Text, [], line, isInMethod));
                    // Also extract expressions from initializers (e.g., await, invocations)
                    if (v.Initializer?.Value != null)
                        ExtractExpressionStatement(v.Initializer.Value, results, line, isInMethod);
                }
                break;
            }
            case ExpressionStatementSyntax exprStmt:
                ExtractExpressionStatement(exprStmt.Expression, results, line, isInMethod);
                break;
            case ThrowStatementSyntax throwStmt:
            {
                string? typeName = throwStmt.Expression is ObjectCreationExpressionSyntax creation
                    ? creation.Type.ToString() : null;
                results.Add(new StatementInfo("throw", [], typeName, null, [], line, isInMethod));
                break;
            }
            case ReturnStatementSyntax returnStmt:
                results.Add(new StatementInfo("return", [], null, null, [], line, isInMethod));
                if (returnStmt.Expression != null)
                    ExtractExpressionStatement(returnStmt.Expression, results, line, isInMethod);
                break;
            case UsingStatementSyntax usingStmt:
                results.Add(new StatementInfo("using", [], null, null, [], line, isInMethod));
                if (usingStmt.Statement is BlockSyntax ub) ExtractStatements(ub, results, isInMethod);
                break;
            case ForEachStatementSyntax forEach:
                results.Add(new StatementInfo("foreach", [], forEach.Type.ToString(),
                    forEach.Identifier.Text, [], line, isInMethod));
                if (forEach.Statement is BlockSyntax fb) ExtractStatements(fb, results, isInMethod);
                break;
            case TryStatementSyntax tryStmt:
                if (tryStmt.Block != null) ExtractStatements(tryStmt.Block, results, isInMethod);
                foreach (var c in tryStmt.Catches)
                {
                    string? caughtType = c.Declaration?.Type.ToString();
                    bool hasRethrow = c.Block != null && c.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any();
                    bool isGeneric = caughtType is null or "Exception" or "System.Exception";
                    results.Add(new StatementInfo("catch", [], caughtType, null, [], LineOf(c), isInMethod) { HasRethrow = hasRethrow, IsErrorHandler = true, IsGenericErrorHandler = isGeneric });
                    if (c.Block != null) ExtractStatements(c.Block, results, isInMethod);
                }
                break;
            case IfStatementSyntax ifStmt:
                if (ifStmt.Statement is BlockSyntax ib) ExtractStatements(ib, results, isInMethod);
                if (ifStmt.Else?.Statement is BlockSyntax eb) ExtractStatements(eb, results, isInMethod);
                break;
            case WhileStatementSyntax ws:
                if (ws.Statement is BlockSyntax wb) ExtractStatements(wb, results, isInMethod);
                break;
            case ForStatementSyntax fs:
                if (fs.Statement is BlockSyntax forb) ExtractStatements(forb, results, isInMethod);
                break;
            case BlockSyntax nested:
                ExtractStatements(nested, results, isInMethod);
                break;
        }
    }

    private static void ExtractExpressionStatement(ExpressionSyntax expr, List<StatementInfo> results, int line, bool isInMethod = true)
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
                results.Add(new StatementInfo("call", [], typeName, memberName, args, line, isInMethod));
                break;
            }
            case AwaitExpressionSyntax awaitExpr:
                var (awaitType, awaitMember, awaitArgs) = ExtractInvocationParts(awaitExpr.Expression);
                results.Add(new StatementInfo("await", [], awaitType, awaitMember, awaitArgs, line, isInMethod));
                ExtractExpressionStatement(awaitExpr.Expression, results, line, isInMethod);
                break;
            case AssignmentExpressionSyntax assignment:
                ExtractExpressionStatement(assignment.Right, results, line, isInMethod);
                break;
            case ObjectCreationExpressionSyntax objCreate:
            {
                var ctorType = ExtractBaseTypeName(objCreate.Type);
                var args = objCreate.ArgumentList?.Arguments
                    .Select(a => a.ToString()).ToList() ?? [];
                results.Add(new StatementInfo("call", [], ctorType, null, args, line, isInMethod));
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

    private static string ExtractBaseTypeName(TypeSyntax type) =>
        type is GenericNameSyntax generic ? generic.Identifier.Text : type.ToString();
}
