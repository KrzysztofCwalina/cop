namespace Cop.Providers.SourceModel;

public record SourceFile(
    string Path,
    string Language,
    List<TypeDeclaration> Types,
    List<StatementInfo> Statements,
    string RawText)
{
    private List<string>? _lines;

    public List<string> Lines => _lines ??= RawText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    public List<string> Usings { get; init; } = [];

    public string? Namespace { get; init; }

    public List<RegionInfo> Regions { get; init; } = [];
}
