// Models fenced Markdown content and its stable content hash.

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
    public string ContentHash { get; } = SourceModel.RegionInfo.ComputeContentHash(Content);
}
