// Represents statements, call bindings, and their structural relationships.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A parsed statement with expression bindings and structural relationships.
/// </summary>
public class StatementInfo(
    string kind,
    List<string> keywords,
    string? typeName,
    string? memberName,
    List<string> arguments,
    int line,
    bool isInMethod)
{
    /// <summary>
    /// Copies source facts and shared graph references without forcing deferred analysis.
    /// </summary>
    public StatementInfo(StatementInfo source)
        : this(source.Kind, source.Keywords, source._typeName, source.MemberName,
            source.Arguments, source.Line, source.IsInMethod)
    {
        SyntaxTypeNameValue = source.SyntaxTypeNameValue;
        Column = source.Column;
        EndLine = source.EndLine;
        EndColumn = source.EndColumn;
        CopyBindingStateFrom(source);
        if (source.DeferredSemantics is not null || source.DeferredTypeNames is not null
            || source._deferredBinding is not null || source._asyncBinding is not null
            || source._asyncTypeNames is not null)
        {
            DeferCopiedAnalysis(source);
        }
        CallTarget = source.CallTarget;
        ValueExpression = source.ValueExpression;
        ConditionExpression = source.ConditionExpression;
        ArgumentExpressions = source.ArgumentExpressions;
        File = source.File;
        HasRethrow = source.HasRethrow;
        IsErrorHandler = source.IsErrorHandler;
        IsGenericErrorHandler = source.IsGenericErrorHandler;
        IsBraced = source.IsBraced;
        CopIgnore = source.CopIgnore;
        Method = source.Method;
        Parent = source.Parent;
        Condition = source.Condition;
        Expression = source.Expression;
        MutableChildren = source.MutableChildren;
    }

    private void CopyBindingStateFrom(StatementInfo source)
    {
        _typeName = source._typeName;
        _receiverType = source._receiverType;
        _valueType = source._valueType;
        _referencedCallable = source._referencedCallable;
        _constructedTypeInterfaces = source._constructedTypeInterfaces;
    }

    private void DeferCopiedAnalysis(StatementInfo source)
    {
        DeferredSemantics = CopyAnalysis(source.DeferredSemantics, source);
        _deferredBinding = CopyAnalysis(source._deferredBinding, source);
        DeferredTypeNames = source.DeferredTypeNames is null ? null : new(() =>
        {
            _typeName = source.TypeName;
            return true;
        });
        if (source._asyncTypeNames is not null)
        {
            _asyncTypeNames = new(async cancellationToken =>
            {
                await source.EnsureTypeNameAnalysisAsync(cancellationToken).ConfigureAwait(false);
                CopyBindingStateFrom(source);
                return true;
            });
        }
        if (source._asyncBinding is not null)
        {
            _asyncBinding = new(async cancellationToken =>
            {
                await source.EnsureSemanticAnalysisAsync(cancellationToken).ConfigureAwait(false);
                CopyBindingStateFrom(source);
                return true;
            });
        }
    }

    private Lazy<bool>? CopyAnalysis(Lazy<bool>? analysis, StatementInfo source) =>
        analysis is null ? null : new(() =>
        {
            bool result = analysis.Value;
            CopyBindingStateFrom(source);
            return result;
        });

    /// <summary>
    /// The one-based source column, or null when the parser cannot provide it.
    /// </summary>
    public int? Column { get; set; }
    /// <summary>
    /// The one-based ending source line, when available.
    /// </summary>
    public int? EndLine { get; set; }
    /// <summary>
    /// The one-based exclusive ending source column, when available.
    /// </summary>
    public int? EndColumn { get; set; }
    /// <summary>
    /// The parsed category of this source element.
    /// </summary>
    public string Kind { get; } = kind;
    /// <summary>
    /// The parsed keywords and modifiers attached to this statement.
    /// </summary>
    public List<string> Keywords { get; } = keywords;
    internal Lazy<bool>? DeferredSemantics { get; set; }
    internal Lazy<bool>? DeferredTypeNames { get; set; }
    private Lazy<bool>? _deferredBinding;
    private AsyncProviderValue<bool>? _asyncBinding;
    private AsyncProviderValue<bool>? _asyncTypeNames;
    private TypeReference? _receiverType;
    private TypeReference? _valueType;
    private CallableReference? _referencedCallable;
    /// <summary>
    /// The bound type of the invocation receiver, when available.
    /// </summary>
    public TypeReference? ReceiverType
    {
        get { _ = _deferredBinding?.Value; return _receiverType; }
        set => _receiverType = value;
    }
    /// <summary>
    /// The bound result type of this expression, when available.
    /// </summary>
    public TypeReference? ValueType
    {
        get { _ = _deferredBinding?.Value; return _valueType; }
        set => _valueType = value;
    }
    /// <summary>
    /// The callable selected by binding this invocation, when available.
    /// </summary>
    public CallableReference? ReferencedCallable
    {
        get { _ = _deferredBinding?.Value; return _referencedCallable; }
        set => _referencedCallable = value;
    }

    /// <summary>Shares provider-scoped binding work; structural fields never demand it.</summary>
    public void DeferSemanticAnalysis(Lazy<bool> analysis) => _deferredBinding = analysis;
    /// <summary>Defers process-backed binding until an asynchronous field read demands it.</summary>
    public void DeferSemanticAnalysisAsync(AsyncProviderValue<bool> analysis) =>
        _asyncBinding = analysis;

    /// <summary>Defers the context required for inferred, rather than syntactic, type names.</summary>
    public void DeferTypeNamePreparationAsync(AsyncProviderValue<bool> analysis) =>
        _asyncTypeNames = analysis;

    /// <summary>Prepares inferred type names without demanding unrelated binding fields.</summary>
    public async ValueTask EnsureTypeNameAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (_asyncTypeNames is not null)
        {
            await _asyncTypeNames.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Awaits this statement's process-backed binding without blocking a getter.</summary>
    public async ValueTask EnsureSemanticAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (_asyncBinding is not null)
        {
            await _asyncBinding.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    private string? _typeName = typeName;
    internal string? SyntaxTypeNameValue { get; } = typeName;
    /// <summary>
    /// The parsed or inferred type name associated with this statement.
    /// </summary>
    public string? TypeName
    {
        get { _ = (DeferredTypeNames ?? DeferredSemantics)?.Value; return _typeName; }
        set => _typeName = value;
    }

    private static readonly Lazy<bool> s_syntaxTypeName = new(() => true);

    /// <summary>A null analysis marks an already-known syntax type without forcing other semantic fields.</summary>
    public void DeferTypeNameAnalysis(Lazy<bool>? analysis) =>
        DeferredTypeNames = analysis ?? s_syntaxTypeName;
    /// <summary>
    /// The declared or invoked member name, when this statement has one.
    /// </summary>
    public string? MemberName { get; } = memberName;
    /// <summary>
    /// The source argument expressions in call order.
    /// </summary>
    public List<string> Arguments { get; } = arguments;
    /// <summary>
    /// The parsed target expression of this invocation, when available.
    /// </summary>
    public ExpressionInfo? CallTarget { get; set; }
    /// <summary>
    /// The parsed value expression, when this statement produces a value.
    /// </summary>
    public ExpressionInfo? ValueExpression { get; set; }
    /// <summary>
    /// The parsed condition expression, when this statement has one.
    /// </summary>
    public ExpressionInfo? ConditionExpression { get; set; }
    /// <summary>
    /// The structured argument expressions in call order.
    /// </summary>
    public List<ExpressionInfo> ArgumentExpressions { get; set; } = [];
    /// <summary>
    /// The one-based source line where this element begins.
    /// </summary>
    public int Line { get; } = line;
    /// <summary>
    /// True when the statement occurs inside a method body.
    /// </summary>
    public bool IsInMethod { get; } = isInMethod;
    /// <summary>
    /// The source file that owns this declaration or source location.
    /// </summary>
    public SourceFile? File { get; set; }
    /// <summary>
    /// True when this handler contains a rethrow of the caught exception.
    /// </summary>
    public bool HasRethrow { get; init; }
    /// <summary>
    /// True when this statement handles an exception or parser error.
    /// </summary>
    public bool IsErrorHandler { get; init; }
    /// <summary>
    /// True when the handler catches an unrestricted exception type.
    /// </summary>
    public bool IsGenericErrorHandler { get; init; }
    /// <summary>
    /// True when this control-flow body is an explicit block.
    /// </summary>
    public bool IsBraced { get; init; }
    /// <summary>
    /// The parsed check-suppression marker associated with this source element.
    /// </summary>
    public string CopIgnore { get; set; } = string.Empty;
    /// <summary>
    /// The display source location used in diagnostics.
    /// </summary>
    public string Source => $"{File?.Path}:{MemberName}";

    // Tree navigation
    /// <summary>
    /// The method containing this source statement, when present.
    /// </summary>
    public MethodDeclaration? Method { get; set; }
    /// <summary>
    /// The immediately enclosing statement, when present.
    /// </summary>
    public StatementInfo? Parent { get; set; }
    /// <summary>
    /// The immediate child elements in source order.
    /// </summary>
    public IReadOnlyList<StatementInfo> Children => MutableChildren;
    /// <summary>
    /// The source spelling of this control-flow condition, when present.
    /// </summary>
    public string? Condition { get; set; }
    /// <summary>
    /// The source spelling of this statement expression, when present.
    /// </summary>
    public string? Expression { get; set; }

    private List<string> _constructedTypeInterfaces = [];
    internal List<string> CachedConstructedTypeInterfaces => _constructedTypeInterfaces;
    /// <summary>
    /// The interfaces implemented by an object created at this call site.
    /// Empty for other statements or when semantic analysis is unavailable.
    /// </summary>
    public List<string> ConstructedTypeInterfaces
    {
        get { _ = DeferredSemantics?.Value; return _constructedTypeInterfaces; }
        set => _constructedTypeInterfaces = value;
    }

    /// <summary>The mutable child list used while constructing statement trees.</summary>
    public List<StatementInfo> MutableChildren { get; set; } = [];

    /// <summary>
    /// Language-specific subtype tag for disk-cache round-tripping (e.g. "csharp"). Null for
    /// the language-agnostic base statement. Subclasses that add language-specific fields
    /// override this together with <see cref="LanguageFlags"/> and register a reconstruction
    /// factory via <see cref="StatementTypeRegistry"/>.
    /// </summary>
    public virtual string? LanguageTag => null;

    /// <summary>Language-specific boolean facts serialized alongside the base statement. Null for the base.</summary>
    public virtual IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => null;

    /// <summary>
    /// Returns enclosing statements from the parent outward.
    /// </summary>
    public List<StatementInfo> GetAncestors()
    {
        List<StatementInfo> result = new List<StatementInfo>();
        StatementInfo? current = Parent;
        while (current != null)
        {
            result.Add(current);
            current = current.Parent;
        }
        return result;
    }
}
