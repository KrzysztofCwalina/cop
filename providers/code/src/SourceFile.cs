namespace Cop.Providers.SourceModel;

public record SourceFile(
    string Path,
    string Language,
    List<TypeDeclaration> Types,
    List<StatementInfo> Statements,
    string RawText)
{
    /// <summary>Absolute source identity, independent of the parser's selected root.</summary>
    public string FullPath { get; init; } = "";
    public string SourceRoot { get; init; } = "";

    private List<string>? _lines;

    public List<string> Lines => _lines ??= RawText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    public List<string> Usings { get; init; } = [];
    public List<ImportInfo> Imports { get; init; } = [];

    /// <summary>Top-level functions, including declarations outside a type.</summary>
    public List<MethodDeclaration> Methods { get; init; } = [];

    public string? Namespace { get; init; }

    public List<RegionInfo> Regions { get; init; } = [];

    /// <summary>
    /// Project names that contain this file. Set by the provider after project discovery.
    /// </summary>
    private List<string>? _projects;
    private Lazy<List<string>>? _deferredProjects;
    public List<string> Projects
    {
        get => _projects ??= _deferredProjects?.Value ?? [];
        set => _projects = value;
    }

    internal void DeferProjects(Func<List<string>> discover)
    {
        _projects = null;
        _deferredProjects = new(discover);
    }

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

    /// <summary>Shares one memoized semantic analysis across this file's syntax-derived objects.</summary>
    public void DeferSemanticAnalysis(Action analyze)
    {
        var semantics = new Lazy<bool>(() => { analyze(); return true; });
        void Attach(TypeDeclaration type)
        {
            type.DeferredSemantics = semantics;
            foreach (var nested in type.NestedTypes) Attach(nested);
        }
        foreach (var type in Types) Attach(type);
        foreach (var statement in Statements)
        {
            statement.DeferredSemantics = semantics;
            statement.DeferSemanticAnalysis(semantics);
        }
    }

    public void DeferTypeNameAnalysis(Action analyze)
    {
        var typeNames = new Lazy<bool>(() => { analyze(); return true; });
        foreach (var statement in Statements) statement.DeferredTypeNames = typeNames;
    }
}
