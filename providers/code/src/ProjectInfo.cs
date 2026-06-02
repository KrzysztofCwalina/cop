namespace Cop.Providers.SourceModel;

/// <summary>
/// Represents a discovered project/package in the workspace.
/// Each language provider populates these from manifest files
/// (e.g., .csproj for C#, package.json for JS, pyproject.toml for Python).
/// </summary>
public record ProjectInfo(
    string Name,
    string Path,
    string? Language,
    List<string> References,
    List<string> Packages,
    List<string> Frameworks)
{
    /// <summary>Backward-compatible constructor for providers compiled against older cop versions.</summary>
    public ProjectInfo(string name, string path, string? language, List<string> references)
        : this(name, path, language, references, [], []) { }
}
