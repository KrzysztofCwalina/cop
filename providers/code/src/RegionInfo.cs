// Represents named source regions and content hashes.

using System.Security.Cryptography;
using System.Text;

namespace Cop.Providers.SourceModel;

/// <summary>
/// A named source region with boundaries and a content fingerprint.
/// </summary>
public record RegionInfo(string Name, int StartLine, int EndLine, string Content)
{
    /// <summary>
    /// The source file that owns this declaration or source location.
    /// </summary>
    public SourceFile? File { get; init; }
    /// <summary>
    /// The display source location used in diagnostics.
    /// </summary>
    public string Source => $"{File?.Path}:{StartLine}";
    /// <summary>
    /// The content fingerprint used to compare parsed regions.
    /// </summary>
    public string ContentHash { get; } = ComputeContentHash(Content);

    /// <summary>
    /// Computes the SHA-256 fingerprint of content after normalizing line endings and trimming.
    /// </summary>
    /// <param name="content">The content to fingerprint.</param>
    /// <returns>The lowercase hexadecimal hash.</returns>
    public static string ComputeContentHash(string content)
    {
        string normalized = content.ReplaceLineEndings("\n").Trim();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }
}
