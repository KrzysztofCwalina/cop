// Records object field initializers and an optional type hint.

namespace Cop.Lang.Ast;

/// <summary>
/// Object literal: { field1: expr1, field2: expr2 } or TypeName { ... }
/// </summary>
public record ObjectExpr(string? TypeHint, List<FieldInit> Fields, int Line = 0) : Expression(Line);
