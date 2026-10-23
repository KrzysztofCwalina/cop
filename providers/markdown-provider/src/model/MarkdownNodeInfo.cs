using Cop.Providers.SourceModel;

namespace Cop.Providers.Markdown;

public sealed class MarkdownNodeInfo(string kind, string text, int level, int startLine)
{
    public string Kind { get; } = kind;
    public string Text { get; } = text;
    public int Level { get; } = level;
    public int StartLine { get; } = startLine;
    public int EndLine { get; internal set; }
    public string Content { get; internal set; } = "";
    public List<MarkdownNodeInfo> Children { get; } = [];
    public SourceFile? File { get; internal set; }
    public string Source => "markdown";
}
