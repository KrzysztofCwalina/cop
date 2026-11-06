// Models a Markdown heading's depth, text, and source location.

namespace Cop.Providers.Markdown;

/// <summary>Represents a Markdown heading and its nesting level.</summary>
public record HeadingInfo(int Level, string Text, int Line)
{
    /// <summary>Gets the Markdown source file containing this heading.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
