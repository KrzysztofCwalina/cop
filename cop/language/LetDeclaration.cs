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
    /// True when this let is a union of other collection lets: let Name = [a, b, c]
    /// where each element references another let-bound collection.
    /// </summary>
    public bool IsCollectionUnion => ValueExpression is ListLiteralExpr list
        && list.Elements.Count > 0 && list.Elements.All(e => e is IdentifierExpr);
}