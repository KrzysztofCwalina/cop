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

public record CollectionUnionExpr(List<Expression> Elements) : Expression;

public record ObjectLiteralExpr(string? TypeName, Dictionary<string, Expression> Fields) : Expression;

public record ConditionalExpr(Expression Condition, Expression TrueExpr, Expression FalseExpr) : Expression;

/// <summary>
/// Multi-branch match expression: discriminant ? pattern1 => result1 | pattern2 => result2 | _ => default
/// </summary>
public record MatchExpr(Expression Discriminant, List<MatchArm> Arms) : Expression;

/// <summary>
/// A single arm in a match expression. Pattern is null for the wildcard (_) arm.
/// </summary>
public record MatchArm(Expression? Pattern, Expression Result);

public record NicExpr() : Expression;

/// <summary>
/// A collection reference with a path override: namespace.Collection('path')
/// Parsed from dotted member access with a single string argument.
/// </summary>
public record PathScopedExpr(Expression Inner, string Path) : Expression;
