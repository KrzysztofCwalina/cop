namespace Cop.Lang;

public abstract record Expression;

public record MemberAccessExpr(Expression Target, string Member) : Expression;

public record PredicateCallExpr(Expression Target, string Name, List<Expression> Args, bool Negated = false) : Expression;

public record IdentifierExpr(string Name) : Expression;

public record BinaryExpr(Expression Left, string Operator, Expression Right) : Expression;

public record UnaryExpr(string Operator, Expression Operand) : Expression;

public record LiteralExpr(object Value) : Expression;

public record FunctionCallExpr(string Name, List<Expression> Args) : Expression;

public record ListLiteralExpr(List<Expression> Elements) : Expression;

public record ObjectLiteralExpr(string? TypeName, Dictionary<string, Expression> Fields) : Expression;

public record ConditionalExpr(Expression Condition, Expression TrueExpr, Expression FalseExpr) : Expression;
