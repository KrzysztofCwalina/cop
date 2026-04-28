namespace Cop.Providers.Markdown;

public record LinkInfo(string Url, string? Text, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
