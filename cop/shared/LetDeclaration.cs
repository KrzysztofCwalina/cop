namespace Cop.Lang;

/// <summary>
/// A let declaration: let Name = BaseCollection:filter1:filter2
/// or a value binding: let Name = ["a", "b", "c"]
/// When ValueExpression is non-null, this is a value binding (BaseCollection/Filters are unused).
/// </summary>
public record LetDeclaration(
    string Name,
    string BaseCollection,
    List<Expression> Filters,
    int Line,
    bool IsExported = false,
    bool IsRuntime = false,
    Expression? ValueExpression = null,
    Expression? Exclusions = null)
{
    public bool IsValueBinding => ValueExpression is not null;

    /// <summary>
    /// True when this let is a Load('path') external document load.
    /// Treated as a collection (not a value binding) at runtime.
    /// </summary>
    public bool IsExternalLoad => ValueExpression is FunctionCallExpr fc && fc.Name == "Load";

    /// <summary>
    /// True when this let is a Parse('file.json', [Type]) JSON/CSV file parse.
    /// Returns a flat typed collection at runtime (unlike Load which returns Documents).
    /// </summary>
    public bool IsFileParse => ValueExpression is FunctionCallExpr fc && fc.Name == "Parse";

    /// <summary>
    /// True when this let is a union of other collections: let Name = a + b + c
    /// </summary>
    public bool IsCollectionUnion => ValueExpression is CollectionUnionExpr;
}