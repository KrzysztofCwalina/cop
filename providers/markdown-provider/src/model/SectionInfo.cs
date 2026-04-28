namespace Cop.Providers.Markdown;

public record SectionInfo(string Heading, int Level, string Content, int StartLine, int EndLine)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{StartLine}";
}
