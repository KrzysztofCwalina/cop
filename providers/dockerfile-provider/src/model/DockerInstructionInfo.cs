namespace Cop.Providers.Dockerfile;

/// <summary>
/// A Dockerfile instruction with its uppercased keyword, trimmed argument, 1-based line number,
/// and 0-based FROM stage index. Instructions before the first FROM use stage -1.
/// </summary>
public record DockerInstructionInfo(string Instruction, string Argument, int Line, int Stage)
{
    public SourceModel.SourceFile? File { get; init; }
    public bool IsJsonForm { get; init; }
    public List<string> Arguments { get; init; } = [];
    public string? From { get; init; }
    public int ReferencedStage { get; init; } = -1;
    public string Source => $"{File?.Path}:{Line}";
}
