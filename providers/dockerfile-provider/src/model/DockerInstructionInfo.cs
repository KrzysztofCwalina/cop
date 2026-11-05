// Models a Dockerfile instruction, its arguments, and referenced build stage.

namespace Cop.Providers.Dockerfile;

/// <summary>
/// A Dockerfile instruction with its uppercased keyword, trimmed argument, 1-based line number,
/// and 0-based FROM stage index. Instructions before the first FROM use stage -1.
/// </summary>
public record DockerInstructionInfo(string Instruction, string Argument, int Line, int Stage)
{
    /// <summary>Gets the Dockerfile containing this instruction.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets whether the instruction uses JSON-array argument syntax.</summary>
    public bool IsJsonForm { get; init; }
    /// <summary>Gets the parsed arguments supplied to the instruction.</summary>
    public List<string> Arguments { get; init; } = [];
    /// <summary>
    /// Gets the source named by a COPY or ADD --from option, if present.
    /// </summary>
    public string? From { get; init; }
    /// <summary>
    /// Gets the referenced stage index, or -1 when no stage is resolved.
    /// </summary>
    public int ReferencedStage { get; init; } = -1;
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
