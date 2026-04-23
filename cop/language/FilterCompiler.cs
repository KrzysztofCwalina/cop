using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Compiles a <see cref="FilterExpression"/> into a native predicate using
/// registered property accessors from the type system.
/// This is the pushdown execution engine — it evaluates provider-level filters
/// without going through the full PredicateEvaluator/ScriptObject pipeline.
/// </summary>
public static class FilterCompiler
{
    /// <summary>
    /// Creates a filter compiler function for a specific type's accessors.
    /// The returned function takes a FilterExpression and produces a Func&lt;object, bool&gt;
    /// that can be applied directly to raw CLR objects.
    /// </summary>
    public static Func<FilterExpression, Func<object, bool>> ForType(
        Dictionary<string, Func<object, object?>> accessors)
    {
        return filter => Compile(filter, accessors);
    }

    /// <summary>
    /// Compiles a single FilterExpression into a Func&lt;object, bool&gt;.
    /// </summary>
    public static Func<object, bool> Compile(
        FilterExpression filter,
        Dictionary<string, Func<object, object?>> accessors)
    {
        return filter switch
        {
            PropertyFilter pf => CompilePropertyFilter(pf, accessors),
            StringOpFilter sf => CompileStringOpFilter(sf, accessors),
            ComparisonFilter cf => CompileComparisonFilter(cf, accessors),
            AndFilter af => CompileAndFilter(af, accessors),
            NotFilter nf => CompileNotFilter(nf, accessors),
            _ => _ => true // Unknown filter type — pass everything through
        };
    }

    private static Func<object, bool> CompilePropertyFilter(
        PropertyFilter pf, Dictionary<string, Func<object, object?>> accessors)
    {
        if (!accessors.TryGetValue(pf.Property, out var accessor))
            return _ => true; // Unknown property — can't filter, pass through

        return item =>
        {
            var value = accessor(item);
            var boolVal = value is true;
            return boolVal == pf.Value;
        };
    }

    private static Func<object, bool> CompileStringOpFilter(
        StringOpFilter sf, Dictionary<string, Func<object, object?>> accessors)
    {
        if (!accessors.TryGetValue(sf.Property, out var accessor))
            return _ => true;

        return item =>
        {
            var value = accessor(item)?.ToString();
            if (value is null) return false;

            return sf.Op switch
            {
                StringOp.StartsWith => value.StartsWith(sf.Value, StringComparison.OrdinalIgnoreCase),
                StringOp.EndsWith => value.EndsWith(sf.Value, StringComparison.OrdinalIgnoreCase),
                StringOp.Contains => value.Contains(sf.Value, StringComparison.OrdinalIgnoreCase),
                StringOp.Equals => value.Equals(sf.Value, StringComparison.OrdinalIgnoreCase),
                StringOp.Matches => System.Text.RegularExpressions.Regex.IsMatch(
                    value, sf.Value, System.Text.RegularExpressions.RegexOptions.None,
                    TimeSpan.FromSeconds(1)),
                _ => true
            };
        };
    }

    private static Func<object, bool> CompileComparisonFilter(
        ComparisonFilter cf, Dictionary<string, Func<object, object?>> accessors)
    {
        if (!accessors.TryGetValue(cf.Property, out var accessor))
            return _ => true;

        return item =>
        {
            var value = accessor(item);
            var numVal = value switch
            {
                int i => (double)i,
                long l => (double)l,
                double d => d,
                float f => (double)f,
                _ => double.NaN
            };

            if (double.IsNaN(numVal)) return false;

            return cf.Op switch
            {
                CompareOp.GreaterThan => numVal > cf.Value,
                CompareOp.LessThan => numVal < cf.Value,
                CompareOp.Equals => Math.Abs(numVal - cf.Value) < 0.001,
                CompareOp.GreaterOrEqual => numVal >= cf.Value,
                CompareOp.LessOrEqual => numVal <= cf.Value,
                _ => true
            };
        };
    }

    private static Func<object, bool> CompileAndFilter(
        AndFilter af, Dictionary<string, Func<object, object?>> accessors)
    {
        var compiled = af.Conditions.Select(c => Compile(c, accessors)).ToArray();
        return item =>
        {
            foreach (var pred in compiled)
                if (!pred(item)) return false;
            return true;
        };
    }

    private static Func<object, bool> CompileNotFilter(
        NotFilter nf, Dictionary<string, Func<object, object?>> accessors)
    {
        var inner = Compile(nf.Inner, accessors);
        return item => !inner(item);
    }
}
