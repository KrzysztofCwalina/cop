// Represents parent-child Markdown block structure with source spans.

using Cop.Providers.SourceModel;

namespace Cop.Providers.Markdown;

/// <summary>Represents a Markdown document or block and its nested children.</summary>
public sealed class MarkdownNodeInfo(string kind, string text, int level, int startLine)
{
    /// <summary>
    /// Gets the block category, such as document, heading, or paragraph.
    /// </summary>
    public string Kind { get; } = kind;
    /// <summary>Gets the block's parsed text.</summary>
    public string Text { get; } = text;
    /// <summary>Gets the heading or nesting level associated with the block.</summary>
    public int Level { get; } = level;
    /// <summary>Gets the first source line occupied by the block.</summary>
    public int StartLine { get; } = startLine;
    /// <summary>Gets the last source line occupied by the block.</summary>
    public int EndLine { get; internal set; }
    /// <summary>Gets the source content covered by the block.</summary>
    public string Content { get; internal set; } = "";
    /// <summary>Gets the immediately nested Markdown blocks.</summary>
    public List<MarkdownNodeInfo> Children { get; } = [];
    /// <summary>Gets the Markdown source file containing the block.</summary>
    public SourceFile? File { get; internal set; }
    /// <summary>Gets the provider identity used for this tree node.</summary>
    public string Source => "markdown";
}
