// Models a Docker build stage and its source location.

namespace Cop.Providers.Dockerfile;

/// <summary>
/// A Dockerfile build stage introduced by a FROM instruction.
/// </summary>
public record DockerStageInfo(string Name, string Image, int Index, int Line)
{
    /// <summary>Gets the Dockerfile declaring this build stage.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
