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
    public string? TypeName { get; } = typeName;
    public string? MemberName { get; } = memberName;
    public List<string> Arguments { get; } = arguments;
    public int Line { get; } = line;
    public bool IsInMethod { get; } = isInMethod;
    public SourceFile? File { get; set; }
    public bool HasRethrow { get; init; }
    public bool IsErrorHandler { get; init; }
    public bool IsGenericErrorHandler { get; init; }
    public string Source => $"{File?.Path}:{MemberName}";

    // Tree navigation
    public MethodDeclaration? Method { get; init; }
    public StatementInfo? Parent { get; set; }
    public IReadOnlyList<StatementInfo> Children => _children;
    public string? Condition { get; init; }
    public string? Expression { get; init; }

    internal List<StatementInfo> _children = [];

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
