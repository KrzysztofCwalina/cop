// A single stage in a pipeline (transform or sink).

namespace Cop.Lang.Ast;

/// <summary>
/// A single stage in a pipeline (transform or sink).
/// </summary>
public record PipelineStage(Expression Expr, int Line = 0) : AstNode(Line);
