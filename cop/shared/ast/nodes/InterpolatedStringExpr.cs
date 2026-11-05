// Records literal and evaluated segments in an interpolated string.

namespace Cop.Lang.Ast;

/// <summary>
/// String interpolation: '{expr} text {expr}'
/// </summary>
public record InterpolatedStringExpr(List<StringPart> Parts, int Line = 0) : Expression(Line);
