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
        var normalized = content.ReplaceLineEndings("\n").Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }
}
