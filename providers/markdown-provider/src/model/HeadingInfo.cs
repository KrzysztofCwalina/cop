namespace Cop.Providers.Markdown;

public record HeadingInfo(int Level, string Text, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
