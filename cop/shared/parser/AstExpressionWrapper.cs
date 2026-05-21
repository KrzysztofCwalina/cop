namespace Cop.Lang;

/// <summary>
/// Wraps a new AST Expression node as an old Cop.Lang.Expression for ScriptFile compatibility.
/// Used during the transition period to bridge the new parser output to legacy consumers.
/// </summary>
public record AstExpressionWrapper(Cop.Lang.Ast.Expression AstExpression) : Expression;
