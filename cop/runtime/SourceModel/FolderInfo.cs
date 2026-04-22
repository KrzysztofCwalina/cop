namespace Cop.Providers.SourceModel;

/// <summary>
/// Represents a folder on disk for filesystem analysis.
/// </summary>
public record FolderInfo(
    string Path,
    string Name,
    bool IsEmpty,
    int FileCount,
    int SubfolderCount,
    int Depth,
    int MinutesSinceModified)
{
    public string Source => Path;
}
