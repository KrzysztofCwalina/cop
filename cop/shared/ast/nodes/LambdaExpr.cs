// Records a lambda expression and its parameter declarations.

namespace Cop.Lang.Ast;

/// <summary>
/// Lambda expression: (params) => body
/// </summary>
public record LambdaExpr(List<Parameter> Params, Expression Body, int Line = 0) : Expression(Line);
