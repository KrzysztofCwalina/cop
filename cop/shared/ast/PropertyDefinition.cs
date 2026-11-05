// Records a declared field's value type and optional computed expression.

namespace Cop.Lang;

/// <summary>
/// Describes a declared field's type, optionality, and computed expression.
/// </summary>
public record PropertyDefinition(
    string Name,
    string TypeName,
    bool IsOptional,
    bool IsCollection,
    int Line,
    Cop.Lang.Ast.Expression? ComputedExpr = null);
