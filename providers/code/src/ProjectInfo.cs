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
    List<string> References);
