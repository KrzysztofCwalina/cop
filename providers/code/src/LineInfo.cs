namespace Cop.Providers.SourceModel;

public record LineInfo(string Text, int Number)
{
    public SourceFile? File { get; init; }
    public string Kind { get; init; } = "code";
    public string CopIgnore { get; init; } = "";
    public string Source => $"{File?.Path}:{Number}";
    public string PreviousText { get; init; } = "";
    public string NextText { get; init; } = "";
}
