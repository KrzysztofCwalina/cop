namespace Cop.Providers.Dockerfile;

/// <summary>
/// A Dockerfile build stage introduced by a FROM instruction.
/// </summary>
public record DockerStageInfo(string Name, string Image, int Index, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
