// Represents source lines, their classification, and attached comments.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A source line with classification, comment, and neighboring-line facts.
/// </summary>
public record LineInfo(string Text, int Number)
{
    /// <summary>
    /// The source file that owns this declaration or source location.
    /// </summary>
    public SourceFile? File { get; init; }
    /// <summary>
    /// The parsed category of this source element.
    /// </summary>
    public string Kind { get; init; } = "code";
    /// <summary>
    /// The parsed check-suppression marker associated with this source element.
    /// </summary>
    public string CopIgnore { get; init; } = string.Empty;
    /// <summary>
    /// The display source location used in diagnostics.
    /// </summary>
    public string Source => $"{File?.Path}:{Number}";
    /// <summary>
    /// The preceding source line, or an empty string at the beginning of the file.
    /// </summary>
    public string PreviousText { get; init; } = string.Empty;
    /// <summary>
    /// The following source line, or an empty string at the end of the file.
    /// </summary>
    public string NextText { get; init; } = string.Empty;
    /// <summary>
    /// The structured comments associated with this source line.
    /// </summary>
    public List<CommentInfo> Comments { get; init; } = [];
}
