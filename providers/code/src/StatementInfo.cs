namespace Cop.Providers.SourceModel;

public class StatementInfo(
    string kind,
    List<string> keywords,
    string? typeName,
    string? memberName,
    List<string> arguments,
    int line,
    bool isInMethod)
{
    public string Kind { get; } = kind;
    public List<string> Keywords { get; } = keywords;
    public string? TypeName { get; set; } = typeName;
    public string? MemberName { get; } = memberName;
    public List<string> Arguments { get; } = arguments;
    public int Line { get; } = line;
    public bool IsInMethod { get; } = isInMethod;
    public SourceFile? File { get; set; }
    public bool HasRethrow { get; init; }
    public bool IsErrorHandler { get; init; }
    public bool IsGenericErrorHandler { get; init; }
    public bool IsBraced { get; init; }
    public string CopIgnore { get; set; } = "";
    public string Source => $"{File?.Path}:{MemberName}";

    // Tree navigation
    public MethodDeclaration? Method { get; init; }
    public StatementInfo? Parent { get; set; }
    public IReadOnlyList<StatementInfo> Children => _children;
    public string? Condition { get; init; }
    public string? Expression { get; init; }

    /// <summary>
    /// For 'call' statements representing object creation (new X()),
    /// this contains all interfaces the constructed type implements (resolved via semantic analysis).
    /// Empty for non-creation statements or when semantic analysis is unavailable.
    /// </summary>
    public List<string> ConstructedTypeInterfaces { get; set; } = [];

    public List<StatementInfo> _children = [];

    public List<StatementInfo> GetAncestors()
    {
        var result = new List<StatementInfo>();
        var current = Parent;
        while (current != null)
        {
            result.Add(current);
            current = current.Parent;
        }
        return result;
    }
}
