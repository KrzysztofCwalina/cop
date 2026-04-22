namespace Cop.Providers.SourceModel;

public record LineInfo(string Text, int Number)
{
    public SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Number}";
}
