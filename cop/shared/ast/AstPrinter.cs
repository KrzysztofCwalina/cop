using System.Text;

namespace Cop.Lang.Ast;

/// <summary>
/// Debug pretty-printer for AST nodes. Produces an indented textual representation
/// useful for verifying parser output and debugging.
/// </summary>
public class AstPrinter : AstVisitor<string>
{
    private int _indent;
    private const int IndentSize = 2;

    protected override string DefaultVisit(AstNode node) => $"{Pad()}{node.GetType().Name}";

    public override string VisitModule(ModuleNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Pad()}Module");
        _indent++;
        foreach (var decl in node.Declarations)
            sb.AppendLine(Visit(decl));
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public override string VisitImportDecl(ImportDecl node) =>
        $"{Pad()}Import: {node.ModuleName}";

    public override string VisitTypeDecl(TypeDecl node)
    {
        var sb = new StringBuilder();
        var export = node.IsExported ? "export " : "";
        var baseType = node.BaseType is not null ? $" : {node.BaseType}" : "";
        sb.AppendLine($"{Pad()}{export}Type: {node.Name}{baseType}");
        _indent++;
        foreach (var prop in node.Properties)
            sb.AppendLine($"{Pad()}{prop.Name}: {FormatTypeRef(prop.Type)}{(prop.IsOptional ? "?" : "")}");
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public override string VisitEnumDecl(EnumDecl node)
    {
        var export = node.IsExported ? "export " : "";
        return $"{Pad()}{export}Enum: {node.Name} = {string.Join(" | ", node.Members)}";
    }

    public override string VisitFlagsDecl(FlagsDecl node)
    {
        var export = node.IsExported ? "export " : "";
        return $"{Pad()}{export}Flags: {node.Name} = {string.Join(" | ", node.Members)}";
    }

    public override string VisitFunctionDecl(FunctionDecl node)
    {
        var sb = new StringBuilder();
        var export = node.IsExported ? "export " : "";
        var @params = string.Join(", ", node.Params.Select(p =>
            p.Type is not null ? $"{p.Name}: {FormatTypeRef(p.Type)}" : p.Name));
        var ret = node.ReturnType is not null ? $" -> {FormatTypeRef(node.ReturnType)}" : "";
        sb.AppendLine($"{Pad()}{export}Function: {node.Name}({@params}){ret}");
        _indent++;
        sb.AppendLine($"{Pad()}Body: {FormatBody(node.Body)}");
        if (node.Guard is not null)
            sb.AppendLine($"{Pad()}Guard: {Visit(node.Guard)}");
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public override string VisitLetDecl(LetDecl node)
    {
        var export = node.IsExported ? "export " : "";
        var type = node.TypeAnnotation is not null ? $" : {FormatTypeRef(node.TypeAnnotation)}" : "";
        return $"{Pad()}{export}Let: {node.Name}{type} = {Visit(node.Value)}";
    }

    public override string VisitCommandDecl(CommandDecl node)
    {
        var sb = new StringBuilder();
        var export = node.IsExported ? "export " : "";
        sb.AppendLine($"{Pad()}{export}Command: {node.Name}");
        _indent++;
        foreach (var stmt in node.Body)
            sb.AppendLine(Visit(stmt));
        _indent--;
        return sb.ToString().TrimEnd();
    }

    // Statements
    public override string VisitLetStatement(LetStatement node)
    {
        var type = node.TypeAnnotation is not null ? $" : {FormatTypeRef(node.TypeAnnotation)}" : "";
        return $"{Pad()}Let: {node.Name}{type} = {Visit(node.Value)}";
    }

    public override string VisitForEachStatement(ForEachStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Pad()}ForEach: {node.Variable} in {Visit(node.Collection)}");
        _indent++;
        foreach (var stmt in node.Body)
            sb.AppendLine(Visit(stmt));
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public override string VisitExpressionStatement(ExpressionStatement node) =>
        $"{Pad()}Expr: {Visit(node.Expr)}";

    public override string VisitPipelineStatement(PipelineStatement node)
    {
        var stages = string.Join(" => ", node.Stages.Select(s => Visit(s.Expr)));
        return $"{Pad()}Pipeline: {Visit(node.Source)} => {stages}";
    }

    // Expressions
    public override string VisitIdentifierExpr(IdentifierExpr node) => node.Name;

    public override string VisitLiteralExpr(LiteralExpr node) => node.Value switch
    {
        null => "null",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        _ => node.Value.ToString() ?? "null"
    };

    public override string VisitBinaryExpr(BinaryExpr node) =>
        $"({Visit(node.Left)} {FormatOp(node.Op)} {Visit(node.Right)})";

    public override string VisitUnaryExpr(UnaryExpr node) =>
        $"{FormatUnaryOp(node.Op)}{Visit(node.Operand)}";

    public override string VisitCallExpr(CallExpr node)
    {
        var args = string.Join(", ", node.Args.Select(Visit));
        return $"{Visit(node.Callee)}({args})";
    }

    public override string VisitMemberExpr(MemberExpr node) =>
        $"{Visit(node.Object)}.{node.Member}";

    public override string VisitIndexExpr(IndexExpr node) =>
        $"{Visit(node.Object)}[{Visit(node.Index)}]";

    public override string VisitLambdaExpr(LambdaExpr node)
    {
        var @params = string.Join(", ", node.Params.Select(p => p.Name));
        return $"({@params}) => {Visit(node.Body)}";
    }

    public override string VisitConditionalExpr(ConditionalExpr node) =>
        $"({Visit(node.Condition)} ? {Visit(node.Then)} : {Visit(node.Else)})";

    public override string VisitMatchExpr(MatchExpr node)
    {
        var arms = string.Join(" | ", node.Arms.Select(a =>
            $"{(a.Pat is WildcardPattern ? "_" : Visit(PatternToExpr(a.Pat)))} => {Visit(a.Body)}"));
        return $"match {Visit(node.Discriminant)} {{ {arms} }}";
    }

    public override string VisitListExpr(ListExpr node)
    {
        var elements = string.Join(", ", node.Elements.Select(Visit));
        return $"[{elements}]";
    }

    public override string VisitObjectExpr(ObjectExpr node)
    {
        var fields = string.Join(", ", node.Fields.Select(f => $"{f.Name}: {Visit(f.Value)}"));
        var type = node.TypeHint is not null ? $"{node.TypeHint} " : "";
        return $"{type}{{ {fields} }}";
    }

    public override string VisitInterpolatedStringExpr(InterpolatedStringExpr node)
    {
        var parts = string.Join("", node.Parts.Select(p => p switch
        {
            TextPart tp => tp.Text,
            ExpressionPart ep => $"{{{Visit(ep.Expr)}}}",
            _ => ""
        }));
        return $"'{parts}'";
    }

    public override string VisitFilterExpr(FilterExpr node)
    {
        var neg = node.Negated ? "!" : "";
        return $"{Visit(node.Collection)}:{neg}{Visit(node.Predicate)}";
    }

    // Helpers
    private string Pad() => new string(' ', _indent * IndentSize);

    private static string FormatTypeRef(TypeRef t) =>
        t.IsCollection ? $"[{t.Name}]" : t.Name;

    private string FormatBody(FunctionBody body) => body switch
    {
        ExpressionBody eb => Visit(eb.Expr),
        MappingBody mb => $"{{ {string.Join(", ", mb.Mappings.Select(m => $"{m.FieldName} = {Visit(m.Value)}"))} }}",
        IntrinsicBody => "intrinsic",
        _ => "<unknown>"
    };

    private static string FormatOp(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.LessThan => "<",
        BinaryOp.GreaterThan => ">",
        BinaryOp.LessOrEqual => "<=",
        BinaryOp.GreaterOrEqual => ">=",
        BinaryOp.And => "&&",
        BinaryOp.Or => "||",
        BinaryOp.BitwiseAnd => "&",
        BinaryOp.BitwiseOr => "|",
        _ => "?"
    };

    private static string FormatUnaryOp(UnaryOp op) => op switch
    {
        UnaryOp.Negate => "-",
        UnaryOp.Not => "!",
        _ => "?"
    };

    private static Expression PatternToExpr(Pattern p) => p switch
    {
        LiteralPattern lp => new LiteralExpr(lp.Value),
        IdentifierPattern ip => new IdentifierExpr(ip.Name),
        WildcardPattern => new IdentifierExpr("_"),
        _ => new IdentifierExpr("<pattern>")
    };
}
