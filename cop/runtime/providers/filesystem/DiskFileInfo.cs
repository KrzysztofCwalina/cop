namespace Cop.Providers.SourceModel;

/// <summary>
/// Represents a file on disk for filesystem analysis.
/// </summary>
public record DiskFileInfo(
    string Path,
    string Name,
    string Extension,
    long Size,
    string Folder,
    int Depth,
    int MinutesSinceModified)
{
    public string Source => Path;

    /// <summary>SHA256 checksum (only populated for locked files).</summary>
    public string? Checksum { get; init; }

    /// <summary>Whether this file is tracked in .cop-lock.</summary>
    public bool IsLocked { get; init; }

    /// <summary>
    /// Lock status: "unlocked" (default), "clean", "modified", "deleted", or "signature-invalid".
    /// </summary>
    public string LockStatus { get; init; } = "unlocked";
}
