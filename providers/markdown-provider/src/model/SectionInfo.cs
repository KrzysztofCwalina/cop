// Models a Markdown heading's associated section content and source span.

namespace Cop.Providers.Markdown;

/// <summary>
/// Represents the content and line span belonging to a Markdown heading.
/// </summary>
public record SectionInfo(string Heading, int Level, string Content, int StartLine, int EndLine)
{
    /// <summary>Gets the Markdown source file containing this section.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and opening line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{StartLine}";
}
