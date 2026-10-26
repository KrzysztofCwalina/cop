using System.Globalization;

namespace Cop.Lang.Ast;

/// <summary>Untruncated, precedence-preserving source for the portable query-expression subset.</summary>
internal static class QuerySourceWriter
{
    public static string Predicate(FunctionDecl declaration)
    {
        return $"predicate {declaration.Name}({string.Join(", ", declaration.Params.Select(Parameter))}) => {Render(((ExpressionBody)declaration.Body).Expr)}";
    }

    private static string Parameter(Parameter parameter) =>
        parameter.Type is null || parameter.Type.Name == parameter.Name && !parameter.Type.IsCollection
            ? parameter.Name : $"{parameter.Name}: {(parameter.Type.IsCollection ? $"[{parameter.Type.Name}]" : parameter.Type.Name)}";

    public static bool CanRender(Expression expression) => expression switch
    {
        IdentifierExpr => true,
        LiteralExpr literal => literal.Value is null or string or bool or int or long
            || literal.Value is double number && double.IsFinite(number),
        BinaryExpr binary => CanRender(binary.Left) && CanRender(binary.Right),
        UnaryExpr unary => CanRender(unary.Operand),
        MemberExpr member => CanRender(member.Object),
        IndexExpr index => CanRender(index.Object) && CanRender(index.Index),
        CallExpr call => CanRender(call.Callee) && call.Args.All(CanRender),
        FilterExpr filter => filter.Predicate is IdentifierExpr or CallExpr { Callee: IdentifierExpr }
            && CanRender(filter.Collection) && CanRender(filter.Predicate),
        LambdaExpr lambda => CanRender(lambda.Body),
        ListExpr list => list.Elements.All(CanRender),
        _ => false
    };

    public static string Render(Expression expression, int parentPrecedence = 0)
    {
        int precedence = Precedence(expression);
        string text = expression switch
        {
            IdentifierExpr id => id.Name,
            LiteralExpr literal => Literal(literal.Value),
            BinaryExpr binary => $"{Render(binary.Left, precedence)} {Operator(binary.Op)} {Render(binary.Right, precedence + 1)}",
            UnaryExpr unary => $"{(unary.Op == UnaryOp.Not ? "!" : "-")}{Render(unary.Operand, precedence)}",
            MemberExpr member => $"{Render(member.Object, precedence)}.{member.Member}",
            IndexExpr index => $"{Render(index.Object, precedence)}[{Render(index.Index)}]",
            CallExpr call => $"{Render(call.Callee, precedence)}({string.Join(", ", call.Args.Select(a => Render(a)))})",
            FilterExpr filter when filter.Predicate is IdentifierExpr or CallExpr { Callee: IdentifierExpr } =>
                $"{Render(filter.Collection, precedence)}:{(filter.Negated ? "!" : "")}{Render(filter.Predicate)}",
            LambdaExpr lambda => $"({string.Join(", ", lambda.Params.Select(Parameter))}) => {Render(lambda.Body)}",
            ListExpr list => $"[{string.Join(", ", list.Elements.Select(e => Render(e)))}]",
            _ => throw new NotSupportedException($"'{expression.GetType().Name}' is not a portable provider-query expression.")
        };
        return precedence < parentPrecedence ? $"({text})" : text;
    }

    private static int Precedence(Expression expression) => expression switch
    {
        LambdaExpr => 0,
        BinaryExpr binary => binary.Op switch
        {
            BinaryOp.Or => 1,
            BinaryOp.And => 2,
            BinaryOp.Equal or BinaryOp.NotEqual => 3,
            BinaryOp.GreaterThan or BinaryOp.GreaterOrEqual or BinaryOp.LessThan or BinaryOp.LessOrEqual => 4,
            BinaryOp.BitwiseOr => 5,
            BinaryOp.BitwiseAnd => 6,
            BinaryOp.Add or BinaryOp.Subtract => 7,
            _ => 8
        },
        UnaryExpr => 9,
        MemberExpr or IndexExpr or CallExpr or FilterExpr => 10,
        LiteralExpr { Value: int i } when i < 0 => 9,
        LiteralExpr { Value: double d } when d < 0 => 9,
        _ => 11
    };

    private static string Literal(object? value) => value switch
    {
        null => "nic",
        bool boolean => boolean ? "true" : "false",
        string text when text.Contains('{') || text.Contains('}') || text.Contains('\r')
            => "@'" + text.Replace("'", "''") + "'",
        string text => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'")
            .Replace("\n", "\\n").Replace("\t", "\\t").Replace("@", "\\@") + "'",
        double number when double.IsFinite(number) => Number(number),
        int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new NotSupportedException($"Literal '{value}' cannot be represented in a portable provider query.")
    };

    private static string Number(double number)
    {
        string text = number.ToString("R", CultureInfo.InvariantCulture);
        int exponentIndex = text.IndexOf('E');
        if (exponentIndex < 0) return text.Contains('.') ? text : text + ".0";
        string mantissa = text[..exponentIndex];
        string sign = mantissa.StartsWith('-') ? "-" : "";
        mantissa = mantissa.TrimStart('-');
        int point = (mantissa.IndexOf('.') is var dot && dot >= 0 ? dot : mantissa.Length)
            + int.Parse(text[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        string digits = mantissa.Replace(".", "");
        return sign + (point <= 0 ? "0." + new string('0', -point) + digits
            : point >= digits.Length ? digits + new string('0', point - digits.Length) + ".0"
            : digits.Insert(point, "."));
    }

    private static string Operator(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.GreaterThan => ">",
        BinaryOp.GreaterOrEqual => ">=",
        BinaryOp.LessThan => "<",
        BinaryOp.LessOrEqual => "<=",
        BinaryOp.And => "&&",
        BinaryOp.Or => "||",
        BinaryOp.BitwiseAnd => "&",
        BinaryOp.BitwiseOr => "|",
        _ => throw new NotSupportedException($"Query operator '{op}' is not supported.")
    };
}
