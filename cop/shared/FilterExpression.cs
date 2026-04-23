namespace Cop.Core;

/// <summary>
/// Represents a filter condition that can be pushed down to a data provider.
/// Analogous to a LINQ expression tree node — providers can inspect and optimize.
/// </summary>
public abstract record FilterExpression
{
    /// <summary>Combines two expressions with AND.</summary>
    public static FilterExpression And(FilterExpression left, FilterExpression right)
    {
        // Flatten nested ANDs
        var conditions = new List<FilterExpression>();
        Flatten(left, conditions);
        Flatten(right, conditions);
        return new AndFilter(conditions);

        static void Flatten(FilterExpression expr, List<FilterExpression> list)
        {
            if (expr is AndFilter and)
                list.AddRange(and.Conditions);
            else
                list.Add(expr);
        }
    }
}

/// <summary>Boolean property check: e.g., Public == true, Abstract == false.</summary>
public record PropertyFilter(string Property, bool Value) : FilterExpression;

/// <summary>String operation on a property: e.g., Name.StartsWith("Client").</summary>
public record StringOpFilter(string Property, StringOp Op, string Value) : FilterExpression;

/// <summary>Numeric comparison: e.g., Size > 1000.</summary>
public record ComparisonFilter(string Property, CompareOp Op, double Value) : FilterExpression;

/// <summary>Conjunction of multiple conditions (all must be true).</summary>
public record AndFilter(List<FilterExpression> Conditions) : FilterExpression;

/// <summary>Negation of an inner expression.</summary>
public record NotFilter(FilterExpression Inner) : FilterExpression;

/// <summary>String operations for <see cref="StringOpFilter"/>.</summary>
public enum StringOp
{
    StartsWith,
    EndsWith,
    Contains,
    Equals,
    Matches
}

/// <summary>Comparison operations for <see cref="ComparisonFilter"/>.</summary>
public enum CompareOp
{
    GreaterThan,
    LessThan,
    Equals,
    GreaterOrEqual,
    LessOrEqual
}
