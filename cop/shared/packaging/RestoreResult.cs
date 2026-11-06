// Reports installed files, processed packages, and warnings from a package restore.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>Result of a package restore operation.</summary>
public class RestoreResult
{
    /// <summary>Number of packages processed.</summary>
    public int PackageCount { get; init; }

    /// <summary>Number of files placed.</summary>
    public int FileCount { get; init; }

    /// <summary>List of files placed during restore.</summary>
    public List<string> PlacedFiles { get; init; } = [];

    /// <summary>Warnings encountered during restore.</summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>Whether the restore succeeded.</summary>
    [JsonPropertyName("Success")]
    public bool IsSuccessful { get; init; }

    /// <summary>Formats the package and file counts for command output.</summary>
    public override string ToString()
    {
        return $"Restored {PackageCount} packages, placed {FileCount} files.";
    }
}
