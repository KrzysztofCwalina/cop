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
    public int? Column { get; set; }
    public int? EndLine { get; set; }
    public int? EndColumn { get; set; }
    public string Kind { get; } = kind;
    public List<string> Keywords { get; } = keywords;
    internal Lazy<bool>? DeferredSemantics { get; set; }
    internal Lazy<bool>? DeferredTypeNames { get; set; }
    private string? _typeName = typeName;
    public string? TypeName
    {
        get { _ = (DeferredTypeNames ?? DeferredSemantics)?.Value; return _typeName; }
        set => _typeName = value;
    }

    private static readonly Lazy<bool> SyntaxTypeName = new(() => true);

    /// <summary>A null analysis marks an already-known syntax type without forcing other semantic fields.</summary>
    public void DeferTypeNameAnalysis(Lazy<bool>? analysis) =>
        DeferredTypeNames = analysis ?? SyntaxTypeName;
    public string? MemberName { get; } = memberName;
    public List<string> Arguments { get; } = arguments;
    public ExpressionInfo? CallTarget { get; set; }
    public ExpressionInfo? ValueExpression { get; set; }
    public List<ExpressionInfo> ArgumentExpressions { get; set; } = [];
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
    public MethodDeclaration? Method { get; set; }
    public StatementInfo? Parent { get; set; }
    public IReadOnlyList<StatementInfo> Children => _children;
    public string? Condition { get; set; }
    public string? Expression { get; set; }

    /// <summary>
    /// For 'call' statements representing object creation (new X()),
    /// this contains all interfaces the constructed type implements (resolved via semantic analysis).
    /// Empty for non-creation statements or when semantic analysis is unavailable.
    /// </summary>
    private List<string> _constructedTypeInterfaces = [];
    internal List<string> CachedConstructedTypeInterfaces => _constructedTypeInterfaces;
    public List<string> ConstructedTypeInterfaces
    {
        get { _ = DeferredSemantics?.Value; return _constructedTypeInterfaces; }
        set => _constructedTypeInterfaces = value;
    }

    public List<StatementInfo> _children = [];

    /// <summary>
    /// Language-specific subtype tag for disk-cache round-tripping (e.g. "csharp"). Null for
    /// the language-agnostic base statement. Subclasses that add language-specific fields
    /// override this together with <see cref="LanguageFlags"/> and register a reconstruction
    /// factory via <see cref="StatementTypeRegistry"/>.
    /// </summary>
    public virtual string? LanguageTag => null;

    /// <summary>Language-specific boolean facts serialized alongside the base statement. Null for the base.</summary>
    public virtual IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => null;

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
