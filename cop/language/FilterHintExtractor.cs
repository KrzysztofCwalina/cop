using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Analyzes a cop filter chain and extracts pushdown-able conditions.
/// Splits filters into a pushdown prefix (passed to the provider) and
/// residual filters (applied locally by the interpreter).
///
/// The extraction stops at "barrier" operations: select, text, user-defined
/// predicates, function maps, or any filter that can't be represented as
/// a simple property check.
/// </summary>
public static class FilterHintExtractor
{
    /// <summary>
    /// Extracts pushdown-able filter hints from a filter chain.
    /// Returns the extracted FilterExpression (or null if none) and the
    /// index of the first non-pushdown filter (residual start).
    /// </summary>
    /// <param name="filters">The cop filter chain (list of Expression nodes).</param>
    /// <param name="itemType">The type descriptor for items in the collection.</param>
    /// <param name="predicateNames">Known user-defined predicate names (these are NOT pushdown-able).</param>
    /// <returns>
    /// (hints, residualStartIndex) — hints is null if no filters can be pushed down.
    /// residualStartIndex is the index where local evaluation should begin.
    /// </returns>
    public static (FilterExpression? Hints, int ResidualStartIndex) Extract(
        List<Expression> filters,
        TypeDescriptor? itemType,
        HashSet<string>? predicateNames = null)
    {
        if (itemType is null || filters.Count == 0)
            return (null, 0);

        var hints = new List<FilterExpression>();
        int i = 0;

        for (; i < filters.Count; i++)
        {
            var filter = filters[i];
            var hint = TryExtract(filter, itemType, predicateNames);
            if (hint is null)
                break; // Hit a barrier — stop extracting

            hints.Add(hint);
        }

        if (hints.Count == 0)
            return (null, 0);

        var combined = hints.Count == 1 ? hints[0] : new AndFilter(hints);
        return (combined, i);
    }

    /// <summary>
    /// Tries to convert a single filter expression to a pushdown FilterExpression.
    /// Returns null if the filter can't be pushed down (barrier).
    /// </summary>
    private static FilterExpression? TryExtract(
        Expression filter,
        TypeDescriptor itemType,
        HashSet<string>? predicateNames)
    {
        switch (filter)
        {
            // Simple identifier: Public, Abstract, Sealed, etc.
            // Pushdown-able if it's a boolean property on the item type.
            case IdentifierExpr id:
                return TryExtractIdentifier(id.Name, negated: false, itemType, predicateNames);

            // Negated identifier: !Public
            case UnaryExpr { Operator: "!" or "not", Operand: IdentifierExpr id }:
                return TryExtractIdentifier(id.Name, negated: true, itemType, predicateNames);

            // Predicate call on item property: Name:startsWith('Client'), Name:contains('Async')
            case PredicateCallExpr pc when IsStringPredicate(pc.Name) && pc.Target is IdentifierExpr prop:
                return TryExtractStringOp(prop.Name, pc.Name, pc.Args, itemType);

            // Everything else is a barrier (user predicates, select, text, function maps, etc.)
            default:
                return null;
        }
    }

    private static FilterExpression? TryExtractIdentifier(
        string name, bool negated,
        TypeDescriptor itemType,
        HashSet<string>? predicateNames)
    {
        // If it's a known user predicate, it's NOT pushdown-able
        if (predicateNames is not null && predicateNames.Contains(name))
            return null;

        // Check if it's a boolean property on the type
        var prop = itemType.GetProperty(name);
        if (prop is null) return null;

        if (prop.TypeName == "bool")
            return new PropertyFilter(name, !negated);

        // Non-boolean properties used as filters aren't pushdown-able
        return null;
    }

    private static FilterExpression? TryExtractStringOp(
        string propertyName, string predicateName, List<Expression> args,
        TypeDescriptor itemType)
    {
        // Must be a string property
        var prop = itemType.GetProperty(propertyName);
        if (prop is null || prop.TypeName != "string") return null;

        // Must have exactly one literal string argument
        if (args.Count != 1 || args[0] is not LiteralExpr literal || literal.Value is not string strValue)
            return null;

        var op = predicateName switch
        {
            "startsWith" => StringOp.StartsWith,
            "endsWith" => StringOp.EndsWith,
            "contains" => StringOp.Contains,
            "matches" => StringOp.Matches,
            _ => (StringOp?)null
        };

        if (op is null) return null;

        return new StringOpFilter(propertyName, op.Value, strValue);
    }

    private static bool IsStringPredicate(string name) =>
        name is "startsWith" or "endsWith" or "contains" or "matches";
}
