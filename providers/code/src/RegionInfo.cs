// Represents named source regions and content hashes.

using System.Security.Cryptography;
using System.Text;

namespace Cop.Providers.SourceModel;

public record RegionInfo(string Name, int StartLine, int EndLine, string Content)
{
    public SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{StartLine}";
    public string ContentHash { get; } = ComputeHash(Content);

    private static string ComputeHash(string content)
    {
        string normalized = content.ReplaceLineEndings("\n").Trim();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }
}
