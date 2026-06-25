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

    /// <summary>
    /// Project names that contain this file. Set by the provider after project discovery.
    /// </summary>
    public List<string> Projects { get; set; } = [];

    /// <summary>
    /// Line numbers (1-based) that are comment lines, populated by source parsers.
    /// </summary>
    public HashSet<int> CommentLines { get; init; } = [];

    /// <summary>
    /// Syntax errors the parser detected in this file. Populated by parsers that can report them
    /// (e.g. the C# parser surfaces Roslyn's syntax diagnostics). The collection builder forwards
    /// these to the engine so a file that fails to parse is reported instead of being silently
    /// skipped or partially/incorrectly modelled.
    /// </summary>
    public List<string> ParseErrors { get; init; } = [];
}
