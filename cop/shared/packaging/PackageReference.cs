namespace Cop.Core;

/// <summary>
/// Represents a fully-qualified Go-style package reference.
/// Example: github.com/org/repo/csharp:1.0.0
/// </summary>
public class PackageReference
{
    public required string FullPath { get; init; }
    public required string Host { get; init; }
    public required string Owner { get; init; }
    public required string Repo { get; init; }
    public required string PackageName { get; init; }
    public string? Version { get; init; }

    public string GitHubApiBaseUrl => $"https://api.github.com/repos/{Owner}/{Repo}";

    public string TagName => Version == null 
        ? throw new InvalidOperationException("Cannot get TagName when Version is null")
        : $"{PackageName}/{Version}";

    public override string ToString()
    {
        if (Version == null)
            return FullPath;
        return $"{FullPath}: {Version}";
    }

    /// <summary>
    /// Parses a Go-style package reference string.
    /// </summary>
    /// <param name="reference">Reference string like "github.com/org/repo/csharp" or "github.com/org/repo/csharp: 1.0.0"</param>
    /// <returns>Parsed PackageReference</returns>
    /// <exception cref="ArgumentException">Thrown when the reference format is invalid</exception>
    public static PackageReference Parse(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("Reference cannot be null or empty", nameof(reference));

        // Split on ": " to separate path from version
        string? version = null;
        string path;

        if (reference.Contains(": "))
        {
            var parts = reference.Split(": ", StringSplitOptions.None);
            if (parts.Length != 2)
                throw new ArgumentException($"Invalid reference format: '{reference}'. Expected format: 'host/owner/repo/package' or 'host/owner/repo/package: version'", nameof(reference));
            
            path = parts[0];
            version = parts[1];
        }
        else
        {
            path = reference;
        }

        // Split the path on "/" to extract components
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (segments.Length < 4)
            throw new ArgumentException($"Invalid reference format: '{reference}'. Path must have at least 4 segments (host/owner/repo/package)", nameof(reference));

        var host = segments[0];
        var owner = segments[1];
        var repo = segments[2];
        var packageName = segments[3];

        return new PackageReference
        {
            FullPath = path,
            Host = host,
            Owner = owner,
            Repo = repo,
            PackageName = packageName,
            Version = version
        };
    }
}
