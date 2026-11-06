// Pipeline: source => transform => sink

namespace Cop.Lang.Ast;

/// <summary>
/// Pipeline: source => transform => sink
/// </summary>
public record PipelineStatement(
    Expression Source,
    List<PipelineStage> Stages,
    int Line = 0) : Statement(Line);
