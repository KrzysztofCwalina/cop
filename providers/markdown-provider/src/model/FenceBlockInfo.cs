// Models fenced Markdown content and its stable content hash.

using System.Security.Cryptography;
using System.Text;

namespace Cop.Providers.Markdown;

/// <summary>
/// Represents a fenced code block with its language, content, and line span.
/// </summary>
public record FenceBlockInfo(string? Language, string? Tag, int StartLine, int EndLine, string Content)
{
    /// <summary>Gets the Markdown source file containing this fence.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and opening line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{StartLine}";
    /// <summary>Gets the SHA-256 hash identifying the fence's content.</summary>
    public string ContentHash { get; } = ComputeHash(Content);

    private static string ComputeHash(string content)
    {
        string normalized = content.ReplaceLineEndings("\n").Trim();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }
}
