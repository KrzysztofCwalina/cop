using System.Text.RegularExpressions;

namespace Cop.Core;

/// <summary>
/// Evaluates a <see cref="FilterExpression"/> against a set of named property values.
/// Providers call <see cref="Matches"/> to test whether a record passes the filter.
/// Throws on type mismatches — e.g., a StringOpFilter on a bool property, or a
/// ComparisonFilter on a string property.
/// </summary>
public static class FilterEvaluator
{
    /// <summary>
    /// Tests whether the given property values satisfy the filter expression.
    /// </summary>
    /// <param name="filter">The filter to evaluate (null = match all).</param>
    /// <param name="getValue">Lookup function: property name → value (string, int/long/double, or bool).
    /// Must throw <see cref="ArgumentException"/> for unrecognized property names.</param>
    /// <returns>true if the record matches the filter (or filter is null).</returns>
    public static bool Matches(FilterExpression? filter, Func<string, object?> getValue)
    {
        if (filter is null) return true;
        return Eval(filter, getValue);
    }

    private static bool Eval(FilterExpression expr, Func<string, object?> getValue) => expr switch
    {
        PropertyFilter pf => EvalBool(pf, getValue(pf.Property)),
        StringOpFilter sf => EvalString(sf, getValue(sf.Property)),
        ComparisonFilter cf => EvalNumeric(cf, getValue(cf.Property)),
        ContainsAnyFilter caf => EvalContainsAny(caf, getValue(caf.Property)),
        InFilter inf => EvalIn(inf, getValue(inf.Property)),
        CollectionContainsFilter ccf => EvalCollectionContains(ccf, getValue(ccf.Property)),
        CollectionAnyFilter canyf => EvalCollectionAny(canyf, getValue(canyf.Property), getValue),
        CollectionCountFilter ccntf => EvalCollectionCount(ccntf, getValue(ccntf.Property)),
        AndFilter af => af.Conditions.All(c => Eval(c, getValue)),
        OrFilter orf => orf.Conditions.Any(c => Eval(c, getValue)),
        NotFilter nf => !Eval(nf.Inner, getValue),
        _ => throw new ArgumentException($"Unknown filter expression type: {expr.GetType().Name}")
    };

    private static bool EvalBool(PropertyFilter pf, object? raw)
    {
        if (raw is not bool b)
            throw new ArgumentException($"PropertyFilter expects bool for '{pf.Property}', got {raw?.GetType().Name ?? "null"}");
        return b == pf.Value;
    }

    private static bool EvalString(StringOpFilter sf, object? raw)
    {
        if (raw is not string s)
            throw new ArgumentException($"StringOpFilter expects string for '{sf.Property}', got {raw?.GetType().Name ?? "null"}");
        return sf.Op switch
        {
            StringOp.Equals => s.Equals(sf.Value, StringComparison.OrdinalIgnoreCase),
            StringOp.StartsWith => s.StartsWith(sf.Value, StringComparison.OrdinalIgnoreCase),
            StringOp.EndsWith => s.EndsWith(sf.Value, StringComparison.OrdinalIgnoreCase),
            StringOp.Contains => s.Contains(sf.Value, StringComparison.OrdinalIgnoreCase),
            StringOp.Matches => Regex.IsMatch(s, sf.Value, RegexOptions.IgnoreCase),
            StringOp.Same => NormalizeIdentifier(s) == NormalizeIdentifier(sf.Value),
            _ => throw new ArgumentException($"Unknown string operation: {sf.Op}")
        };
    }

    private static bool EvalContainsAny(ContainsAnyFilter caf, object? raw)
    {
        if (raw is not string s)
            throw new ArgumentException($"ContainsAnyFilter expects string for '{caf.Property}', got {raw?.GetType().Name ?? "null"}");
        foreach (var value in caf.Values)
        {
            if (s.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool EvalIn(InFilter inf, object? raw)
    {
        var s = raw?.ToString() ?? "";
        foreach (var value in inf.Values)
        {
            if (s.Equals(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool EvalNumeric(ComparisonFilter cf, object? raw)
    {
        if (raw is not (int or long or double or float))
            throw new ArgumentException($"ComparisonFilter expects numeric for '{cf.Property}', got {raw?.GetType().Name ?? "null"}");
        var num = AsDouble(raw);
        return cf.Op switch
        {
            CompareOp.Equals => num == cf.Value,
            CompareOp.GreaterThan => num > cf.Value,
            CompareOp.LessThan => num < cf.Value,
            CompareOp.GreaterOrEqual => num >= cf.Value,
            CompareOp.LessOrEqual => num <= cf.Value,
            _ => throw new ArgumentException($"Unknown comparison operation: {cf.Op}")
        };
    }

    private static double AsDouble(object? v) => v switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        _ => 0
    };

    private static bool EvalCollectionContains(CollectionContainsFilter ccf, object? raw)
    {
        if (raw is not System.Collections.IList list)
            return false;
        foreach (var item in list)
        {
            if (string.Equals(item?.ToString(), ccf.Value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool EvalCollectionAny(CollectionAnyFilter canyf, object? raw, Func<string, object?> parentGetValue)
    {
        if (raw is not System.Collections.IList list)
            return false;
        // Each item in the collection is evaluated against the item filter.
        // The getValue callback for sub-items delegates to the item's own properties —
        // but at the FilterEvaluator level we only have the parent's getValue.
        // For provider-level pushdown, the provider must supply a getValue that handles
        // collection items. For now, return true (pass through) if we can't evaluate sub-items.
        // The full collection-any pushdown is handled by FilterCompiler which has type accessors.
        return true;
    }

    private static bool EvalCollectionCount(CollectionCountFilter ccntf, object? raw)
    {
        if (raw is not System.Collections.IList list)
            return false;
        int count = list.Count;
        return ccntf.Op switch
        {
            CompareOp.Equals => count == ccntf.Value,
            CompareOp.GreaterThan => count > ccntf.Value,
            CompareOp.LessThan => count < ccntf.Value,
            CompareOp.GreaterOrEqual => count >= ccntf.Value,
            CompareOp.LessOrEqual => count <= ccntf.Value,
            _ => throw new ArgumentException($"Unknown comparison operation: {ccntf.Op}")
        };
    }

    /// <summary>
    /// Normalize an identifier for convention-insensitive comparison (sm predicate).
    /// Strips underscores, hyphens, and lowercases everything.
    /// </summary>
    private static string NormalizeIdentifier(string s) =>
        s.Replace("_", "").Replace("-", "").ToLowerInvariant();
}
