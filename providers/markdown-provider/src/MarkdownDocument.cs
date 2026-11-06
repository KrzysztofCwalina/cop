// Cached parse result for a single markdown file.

namespace Cop.Providers.Markdown;

/// <summary>
/// Cached parse result for a single markdown file.
/// </summary>
public record MarkdownDocument(
    List<HeadingInfo> Headings,
    List<LinkInfo> Links,
    List<FenceBlockInfo> FenceBlocks,
    List<SectionInfo> Sections)
{
    /// <summary>Gets the root of the parsed Markdown document tree.</summary>
    public MarkdownNodeInfo Root { get; init; } = new("document", string.Empty, 0, 1);
}
