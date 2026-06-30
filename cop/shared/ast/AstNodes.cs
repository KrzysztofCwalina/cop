namespace Cop.Lang.Ast;

// ============================================================================
// AST Root
// ============================================================================

/// <summary>
/// Base class for all AST nodes. Every node carries a source location for diagnostics.
/// </summary>
public abstract record AstNode(int Line = 0);

// ============================================================================
// Module
// ============================================================================

/// <summary>
/// Root node representing a source file / module.
/// </summary>
public record ModuleNode(List<Declaration> Declarations, int Line = 0) : AstNode(Line);

// ============================================================================
// Declarations (top-level)
// ============================================================================

public abstract record Declaration(int Line = 0) : AstNode(Line);

/// <summary>
/// Import declaration: import ModuleName
/// </summary>
public record ImportDecl(string ModuleName, int Line = 0) : Declaration(Line);

/// <summary>
/// Type declaration with optional base type and properties.
/// </summary>
public record TypeDecl(
    string Name,
    string? BaseType,
    List<PropertyDecl> Properties,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0,
    List<string>? Traits = null) : Declaration(Line);

/// <summary>
/// A single property within a type declaration.
/// For computed properties (e.g., filePath => item.File.Path), ComputedExpr is set
/// and Type holds an inferred placeholder.
/// </summary>
public record PropertyDecl(
    string Name,
    TypeRef Type,
    bool IsOptional = false,
    int Line = 0,
    Expression? ComputedExpr = null) : AstNode(Line);

/// <summary>
/// Enum declaration: enum Name = Member1 | Member2 | ...
/// </summary>
public record EnumDecl(
    string Name,
    TypeRef? MemberType,
    List<string> Members,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);

/// <summary>
/// Flags declaration: flags Name = Flag1 | Flag2 | ...
/// </summary>
public record FlagsDecl(
    string Name,
    List<string> Members,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);

/// <summary>
/// Function declaration. Predicates are functions returning bool.
/// Supports both expression body and field-mapping body (for transforms).
/// </summary>
public record FunctionDecl(
    string Name,
    List<Parameter> Params,
    TypeRef? ReturnType,
    FunctionBody Body,
    bool IsExported = false,
    Expression? Guard = null,
    string? DocComment = null,
    int Line = 0,
    bool IsPredicate = false) : Declaration(Line)
{
    /// <summary>
    /// True when this function is a runnable command: an uppercase-named function with a block body.
    /// This is the single source of truth for "is a command" — tooling (REPL, help, list, run) must
    /// consult this instead of re-deriving <c>char.IsUpper(Name[0]) &amp;&amp; Body is BlockBody</c>.
    /// </summary>
    public bool IsCommand => Name.Length > 0 && char.IsUpper(Name[0]) && Body is BlockBody;
}

/// <summary>
/// Let binding: let Name : Type = Expression
/// </summary>
public record LetDecl(
    string Name,
    TypeRef? TypeAnnotation,
    Expression Value,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);

/// <summary>
/// Command declaration (deprecated sugar for uppercase function with block body).
/// Retained for backward compatibility with old parser; new code should use FunctionDecl with BlockBody.
/// </summary>
public record CommandDecl(
    string Name,
    List<Statement> Body,
    List<string>? Parameters = null,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);

// ============================================================================
// Statements (inside commands / blocks)
// ============================================================================

public abstract record Statement(int Line = 0) : AstNode(Line);

/// <summary>
/// Local let binding within a block.
/// </summary>
public record LetStatement(string Name, TypeRef? TypeAnnotation, Expression Value, int Line = 0) : Statement(Line);

/// <summary>
/// For-each iteration: foreach item in collection { body }
/// </summary>
public record ForEachStatement(
    string Variable,
    Expression Collection,
    List<Statement> Body,
    int Line = 0,
    bool IsAsync = false) : Statement(Line);

/// <summary>
/// Expression used as a statement (e.g., a function call with side effects).
/// </summary>
public record ExpressionStatement(Expression Expr, int Line = 0) : Statement(Line);

/// <summary>
/// Pipeline: source => transform => sink
/// </summary>
public record PipelineStatement(
    Expression Source,
    List<PipelineStage> Stages,
    int Line = 0) : Statement(Line);

/// <summary>
/// A single stage in a pipeline (transform or sink).
/// </summary>
public record PipelineStage(Expression Expr, int Line = 0) : AstNode(Line);

// ============================================================================
// Expressions
// ============================================================================

public abstract record Expression(int Line = 0) : AstNode(Line);

/// <summary>
/// Simple identifier reference: x, myFunc, Type
/// </summary>
public record IdentifierExpr(string Name, int Line = 0) : Expression(Line);

/// <summary>
/// Literal value: 42, 'hello', true, null
/// </summary>
public record LiteralExpr(object? Value, int Line = 0) : Expression(Line);

/// <summary>
/// Binary operation: left op right
/// </summary>
public record BinaryExpr(Expression Left, BinaryOp Op, Expression Right, int Line = 0) : Expression(Line);

/// <summary>
/// Unary operation: !x, -x
/// </summary>
public record UnaryExpr(UnaryOp Op, Expression Operand, int Line = 0) : Expression(Line);

/// <summary>
/// Function/method call: callee(args) or obj.method(args)
/// </summary>
public record CallExpr(Expression Callee, List<Expression> Args, int Line = 0) : Expression(Line);

/// <summary>
/// Member access: obj.member
/// </summary>
public record MemberExpr(Expression Object, string Member, int Line = 0) : Expression(Line);

/// <summary>
/// Index access: obj[index]
/// </summary>
public record IndexExpr(Expression Object, Expression Index, int Line = 0) : Expression(Line);

/// <summary>
/// Lambda expression: (params) => body
/// </summary>
public record LambdaExpr(List<Parameter> Params, Expression Body, int Line = 0) : Expression(Line);

/// <summary>
/// Ternary conditional: cond ? then : else
/// </summary>
public record ConditionalExpr(Expression Condition, Expression Then, Expression Else, int Line = 0) : Expression(Line);

/// <summary>
/// Match expression: expr ? pattern1 => result1 | pattern2 => result2
/// </summary>
public record MatchExpr(Expression Discriminant, List<MatchArm> Arms, int Line = 0) : Expression(Line);

/// <summary>
/// List literal: [a, b, c]
/// </summary>
public record ListExpr(List<Expression> Elements, int Line = 0) : Expression(Line);

/// <summary>
/// Object literal: { field1: expr1, field2: expr2 } or TypeName { ... }
/// </summary>
public record ObjectExpr(string? TypeHint, List<FieldInit> Fields, int Line = 0) : Expression(Line);

/// <summary>
/// String interpolation: '{expr} text {expr}'
/// </summary>
public record InterpolatedStringExpr(List<StringPart> Parts, int Line = 0) : Expression(Line);

/// <summary>
/// Filter application: collection:predicate (syntactic sugar for filtering)
/// </summary>
public record FilterExpr(Expression Collection, Expression Predicate, bool Negated = false, int Line = 0) : Expression(Line);

/// <summary>
/// A foreach used in expression position (e.g. as an operand of `&`). Runs the wrapped loop for
/// its side effects (auto-printed body results) and evaluates to nil. Lets `print(a) & foreach
/// xs => print(item)` chain a loop after other output-producing expressions.
/// </summary>
public record ForEachExpr(ForEachStatement Loop, int Line = 0) : Expression(Line);

// ============================================================================
// Supporting Types
// ============================================================================

/// <summary>
/// Function/lambda parameter.
/// </summary>
public record Parameter(string Name, TypeRef? Type = null, int Line = 0) : AstNode(Line);

/// <summary>
/// Type reference (used in annotations).
/// Constraint is used for type parameter bounds: [T:comparable] → Constraint="comparable"
/// </summary>
public record TypeRef(string Name, bool IsCollection = false, int Line = 0, string? Constraint = null) : AstNode(Line);

/// <summary>
/// A single arm in a match expression.
/// </summary>
public record MatchArm(Pattern Pat, Expression Body, int Line = 0) : AstNode(Line);

/// <summary>
/// Field initializer in an object literal.
/// </summary>
public record FieldInit(string Name, Expression Value, int Line = 0) : AstNode(Line);

// ============================================================================
// Patterns (for match expressions)
// ============================================================================

public abstract record Pattern(int Line = 0) : AstNode(Line);

/// <summary>
/// Literal pattern: matches a specific value.
/// </summary>
public record LiteralPattern(object Value, int Line = 0) : Pattern(Line);

/// <summary>
/// Wildcard pattern: _ (matches anything).
/// </summary>
public record WildcardPattern(int Line = 0) : Pattern(Line);

/// <summary>
/// Identifier pattern: binds matched value to a name.
/// </summary>
public record IdentifierPattern(string Name, int Line = 0) : Pattern(Line);

// ============================================================================
// String Interpolation Parts
// ============================================================================

public abstract record StringPart;

/// <summary>
/// Literal text segment in an interpolated string.
/// </summary>
public record TextPart(string Text) : StringPart;

/// <summary>
/// Expression segment in an interpolated string: {expr}
/// </summary>
public record ExpressionPart(Expression Expr, string? Format = null) : StringPart;

// ============================================================================
// Function Bodies
// ============================================================================

public abstract record FunctionBody;

/// <summary>
/// Function body is a single expression: fn add(a, b) = a + b
/// </summary>
public record ExpressionBody(Expression Expr) : FunctionBody;

/// <summary>
/// Function body is a set of field mappings (for transform functions):
/// fn toSummary(item: Item) -> Summary =
///     Name = item.Name
///     Count = item.Items.count()
/// </summary>
public record MappingBody(List<FieldMapping> Mappings) : FunctionBody;

/// <summary>
/// Marker: implementation is provided by the runtime (intrinsic).
/// </summary>
public record IntrinsicBody() : FunctionBody;

/// <summary>
/// Function body is a statement block (only for ALL-UPPERCASE function names):
/// function MAIN() = { foreach items => print(item.Name) }
/// </summary>
public record BlockBody(List<Statement> Statements) : FunctionBody;

/// <summary>
/// A single field mapping in a MappingBody.
/// </summary>
public record FieldMapping(string FieldName, Expression Value, int Line = 0) : AstNode(Line);

// ============================================================================
// Operators
// ============================================================================

public enum BinaryOp
{
    // Arithmetic
    Add,        // +
    Subtract,   // -
    Multiply,   // *
    Divide,     // /
    Modulo,     // %

    // Comparison
    Equal,          // ==
    NotEqual,       // !=
    LessThan,       // <
    GreaterThan,    // >
    LessOrEqual,    // <=
    GreaterOrEqual, // >=

    // Logical
    And,    // &&
    Or,     // ||

    // Bitwise
    BitwiseAnd, // &
    BitwiseOr,  // |
}

public enum UnaryOp
{
    Negate, // -
    Not,    // !
}
