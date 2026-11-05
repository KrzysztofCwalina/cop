// Parses fully qualified package references and derives their release identifiers.

namespace Cop.Core;

/// <summary>
/// Represents a fully-qualified Go-style package reference.
/// Example: github.com/org/repo/csharp:1.0.0
/// </summary>
public class PackageReference
{
    /// <summary>The package path without its optional version suffix.</summary>
    public required string FullPath { get; init; }

    /// <summary>The source host from the first package-path segment.</summary>
    public required string Host { get; init; }

    /// <summary>The account or organization owning the source repository.</summary>
    public required string Owner { get; init; }

    /// <summary>The source repository name within the owner's account.</summary>
    public required string Repo { get; init; }

    /// <summary>The package directory name within the source repository.</summary>
    public required string PackageName { get; init; }

    /// <summary>The requested version, or null when no version was specified.</summary>
    public string? Version { get; init; }

    /// <summary>The GitHub REST API root for the source repository.</summary>
    public string GitHubApiBaseUrl => $"https://api.github.com/repos/{Owner}/{Repo}";

    /// <summary>The package-specific release tag for a versioned reference.</summary>
    public string TagName => Version == null 
        ? throw new InvalidOperationException("Cannot get TagName when Version is null")
        : $"{PackageName}/{Version}";

    /// <summary>
    /// Formats the package path and appends the version when specified.
    /// </summary>
    public override string ToString()
    {
        if (Version == null)
        {
            return FullPath;
        }

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
        {
            throw new ArgumentException("Reference cannot be null or empty", nameof(reference));
        }

        // The version is separated from the path by ':' (with or without surrounding spaces),
        // e.g. "host/owner/repo/pkg:1.0.0" or "host/owner/repo/pkg: 1.0.0". Path segments are
        // separated by '/' and never contain ':', so split on the first ':'.
        string? version = null;
        string path;

        int colon = reference.IndexOf(':');
        if (colon >= 0)
        {
            path = reference[..colon].Trim();
            string rest = reference[(colon + 1)..].Trim();
            version = rest.Length > 0 ? rest : null;
        }
        else
        {
            path = reference.Trim();
        }

        // Split the path on "/" to extract components
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (segments.Length < 4)
        {
            throw new ArgumentException($"Invalid reference format: '{reference}'. Path must have at least 4 segments (host/owner/repo/package)", nameof(reference));
        }

        string host = segments[0];
        string owner = segments[1];
        string repo = segments[2];
        string packageName = segments[3];

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
