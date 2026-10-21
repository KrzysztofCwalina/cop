using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Maps the Cop AST to the shared code source model.
/// </summary>
public class CopSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".cop"];
    public override string Language => "cop";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        ModuleNode module;
        try
        {
            module = Cop.Lang.Parser.CopParser.Parse(sourceText, filePath);
        }
        catch (ParseException)
        {
            // Return a minimal source file so Lines/Files still work
            return new SourceFile(filePath, "cop", [], [], sourceText);
        }

        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var usings = module.Declarations.OfType<ImportDecl>().Select(i => i.ModuleName).ToList();

        // Map type definitions → TypeDeclaration
        foreach (var td in module.Declarations.OfType<TypeDecl>())
        {
            var modifiers = td.IsExported ? Modifier.Public : Modifier.None;
            var fields = td.Properties.Select(p =>
                new FieldDeclaration(p.Name, MakeTypeRef(p.Type.Name, p.Type.IsCollection), Modifier.Public, p.Line))
                .ToList();
            var baseTypes = td.BaseType is not null ? new List<string> { td.BaseType } : [];

            types.Add(new TypeDeclaration(td.Name, TypeKind.Class, modifiers,
                baseTypes, [], [], [], [], [], td.Line)
            {
                Fields = fields
            });
        }

        // Map flags definitions → TypeDeclaration (Enum)
        foreach (var fd in module.Declarations.OfType<FlagsDecl>())
        {
            var modifiers = fd.IsExported ? Modifier.Public : Modifier.None;
            types.Add(new TypeDeclaration(fd.Name, TypeKind.Enum, modifiers,
                [], [], [], [], [], fd.Members, fd.Line));
        }

        // Map enum definitions → TypeDeclaration (Enum)
        foreach (var ed in module.Declarations.OfType<EnumDecl>())
        {
            var modifiers = ed.IsExported ? Modifier.Public : Modifier.None;
            types.Add(new TypeDeclaration(ed.Name, TypeKind.Enum, modifiers,
                [], [], [], [], [], ed.Members, ed.Line));
        }

        foreach (var fn in module.Declarations.OfType<FunctionDecl>())
        {
            var keywords = new List<string> { fn.IsPredicate ? "predicate" : "function" };
            if (fn.Body is IntrinsicBody) keywords.Add("intrinsic");
            statements.Add(new StatementInfo(fn.IsCommand ? "command" : "declaration", keywords,
                fn.IsPredicate ? fn.Params.FirstOrDefault()?.Type?.Name : fn.ReturnType?.Name,
                fn.Name, [], fn.Line, false));
        }

        // Map import statements
        foreach (var imp in module.Declarations.OfType<ImportDecl>())
        {
            statements.Add(new StatementInfo("import", [], null, imp.ModuleName, [], imp.Line, false));
        }

        // Map let declarations
        foreach (var let in module.Declarations.OfType<LetDecl>())
        {
            var keywords = new List<string> { "let" };
            if (let.IsExported) keywords.Add("export");
            statements.Add(new StatementInfo("declaration", keywords,
                null, let.Name, [], let.Line, false));
        }

        // Map commands → statements
        foreach (var cmd in module.Declarations.OfType<CommandDecl>())
        {
            statements.Add(new StatementInfo("command", [],
                null, cmd.Name, [], cmd.Line, false));
        }

        new ExpressionCollector(statements).Visit(module);

        return new SourceFile(filePath, "cop", types, statements, sourceText)
        {
            Usings = usings
        };
    }

    private sealed class ExpressionCollector(List<StatementInfo> statements) : AstWalker
    {
        public override object? VisitCallExpr(Cop.Lang.Ast.CallExpr node)
        {
            var target = ConvertExpression(node.Callee);
            statements.Add(new StatementInfo("call", [],
                target.Target?.Name, target.Name, [], node.Line, false)
            {
                CallTarget = target,
                ArgumentExpressions = node.Args.Select(ConvertExpression).ToList()
            });
            return base.VisitCallExpr(node);
        }

        public override object? VisitFunctionDecl(FunctionDecl node)
        {
            if (node.Body is BlockBody block)
                foreach (var statement in block.Statements) Visit(statement);
            return base.VisitFunctionDecl(node);
        }

        protected override object? DefaultVisit(AstNode node)
        {
            if (node is PropertyDecl { ComputedExpr: not null } property)
                Visit(property.ComputedExpr);
            else if (node is ForEachExpr loop)
                Visit(loop.Loop);
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
        _ => new("other")
    };

    private static TypeReference MakeTypeRef(string typeName, bool isCollection = false)
    {
        if (isCollection)
        {
            var inner = new TypeReference(typeName, null, [], typeName);
            return new TypeReference("List", null, [inner], $"[{typeName}]");
        }
        return new TypeReference(typeName, null, [], typeName);
    }
}
