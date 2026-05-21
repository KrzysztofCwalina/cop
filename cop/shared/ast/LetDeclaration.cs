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
    Expression? ValueExpression = null,
    Expression? Exclusions = null,
    Expression? SourceExpression = null,
    string? PathOverride = null,
    string? DocComment = null,
    string? PackageName = null,
    string? TypeAnnotation = null)
{
    public bool IsValueBinding => ValueExpression is not null;

    /// <summary>
    /// True when this let is a union of other collections: let Name = a + b + c
    /// </summary>
    public bool IsCollectionUnion => ValueExpression is CollectionUnionExpr;
}