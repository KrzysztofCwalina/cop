// A changed file with its paths, status, patch, and affected line ranges.

namespace Cop.Core;

/// <summary>
/// A changed file with its paths, status, patch, and affected line ranges.
/// </summary>
public sealed record ChangedFile(
    string Path,
    string? OldPath,
    string Status,
    string Patch,
    IReadOnlyList<ChangedRange> Ranges,
    bool IsBinary)
{
    /// <summary>
    /// Whether the change removes the file from the head revision.
    /// </summary>
    public bool IsDeleted => Status == "D";
}
