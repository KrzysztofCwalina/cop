namespace Cop.Lang.Ast;

using System.Globalization;

/// <summary>
/// Renders an AST <see cref="Expression"/> back to a readable, cop-like source string.
/// Used for diagnostics (e.g. the <c>-d</c> trace output) so a query reads as
/// <c>codebase.Types:isPublic</c> rather than an internal node type name like
/// <c>CallExpr</c>. Optimized for readability — not guaranteed to be an exact round-trip.
/// </summary>
public static class ExpressionRenderer
{
    /// <summary>
    /// Renders <paramref name="expr"/> to a cop-like source string, truncated to
    /// <paramref name="maxLength"/> characters (with an ellipsis) to keep trace lines compact.
    /// </summary>
    public static string Render(Expression? expr, int maxLength = 120)
    {
        var text = RenderCore(expr);
        if (maxLength > 0 && text.Length > maxLength)
            text = text.Substring(0, maxLength - 1) + "\u2026";
        return text;
    }

    private static string RenderCore(Expression? expr) => expr switch
    {
        null => "?",
        IdentifierExpr e => e.Name,
        LiteralExpr e => RenderLiteral(e.Value),
        MemberExpr e => $"{RenderCore(e.Object)}.{e.Member}",
        CallExpr e => $"{RenderCore(e.Callee)}({string.Join(", ", e.Args.Select(RenderCore))})",
        FilterExpr e => $"{RenderCore(e.Collection)}:{(e.Negated ? "!" : "")}{RenderCore(e.Predicate)}",
        BinaryExpr e => $"{RenderCore(e.Left)} {RenderBinaryOp(e.Op)} {RenderCore(e.Right)}",
        UnaryExpr e => $"{RenderUnaryOp(e.Op)}{RenderCore(e.Operand)}",
        IndexExpr e => $"{RenderCore(e.Object)}[{RenderCore(e.Index)}]",
        LambdaExpr e => $"({string.Join(", ", e.Params.Select(p => p.Name))}) => {RenderCore(e.Body)}",
        ConditionalExpr e => $"{RenderCore(e.Condition)} ? {RenderCore(e.Then)} : {RenderCore(e.Else)}",
        ListExpr e => $"[{string.Join(", ", e.Elements.Select(RenderCore))}]",
        ObjectExpr e => $"{(e.TypeHint is null ? "" : e.TypeHint + " ")}{{{string.Join(", ", e.Fields.Select(f => $"{f.Name} = {RenderCore(f.Value)}"))}}}",
        InterpolatedStringExpr e => RenderInterpolated(e),
        MatchExpr e => $"{RenderCore(e.Discriminant)} ? <{e.Arms.Count} arm(s)>",
        _ => expr.GetType().Name
    };

    private static string RenderLiteral(object? value) => value switch
    {
        null => "null",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?"
    };

    private static string RenderInterpolated(InterpolatedStringExpr e)
    {
        var inner = string.Concat(e.Parts.Select(p => p switch
        {
            TextPart t => t.Text,
            ExpressionPart ep => $"{{{RenderCore(ep.Expr)}{(ep.Format is null ? "" : "@" + ep.Format)}}}",
            _ => ""
        }));
        return $"'{inner}'";
    }

    private static string RenderBinaryOp(BinaryOp op) => op switch
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

    private static string RenderUnaryOp(UnaryOp op) => op switch
    {
        UnaryOp.Negate => "-",
        UnaryOp.Not => "!",
        _ => "?"
    };
}
