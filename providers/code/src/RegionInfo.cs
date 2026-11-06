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
    public string ContentHash { get; } = ComputeHash(Content);

    private static string ComputeHash(string content)
    {
        string normalized = content.ReplaceLineEndings("\n").Trim();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }
}
