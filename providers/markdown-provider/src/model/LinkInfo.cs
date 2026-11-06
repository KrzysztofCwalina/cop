// Models a Markdown link target, label, and source location.

namespace Cop.Providers.Markdown;

/// <summary>Represents a Markdown link target and its optional label.</summary>
public record LinkInfo(string Url, string? Text, int Line)
{
    /// <summary>Gets the Markdown source file containing this link.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
