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

    /// <summary>Combines two expressions with OR.</summary>
    public static FilterExpression Or(FilterExpression left, FilterExpression right)
    {
        // Flatten nested ORs
        var conditions = new List<FilterExpression>();
        Flatten(left, conditions);
        Flatten(right, conditions);
        return new OrFilter(conditions);

        static void Flatten(FilterExpression expr, List<FilterExpression> list)
        {
            if (expr is OrFilter or)
                list.AddRange(or.Conditions);
            else
                list.Add(expr);
        }
    }

    /// <summary>Formats a filter expression as a human-readable string for diagnostics.</summary>
    public static string Format(FilterExpression filter) => filter switch
    {
        PropertyFilter pf => pf.Value ? pf.Property : $"!{pf.Property}",
        StringOpFilter sf => $"{sf.Property}:{sf.Op switch
        {
            StringOp.StartsWith => "sw",
            StringOp.EndsWith => "ew",
            StringOp.Contains => "ct",
            StringOp.Equals => "eq",
            StringOp.Matches => "rx",
            StringOp.Same => "sm",
            _ => sf.Op.ToString()
        }}('{sf.Value}')",
        ComparisonFilter cf => $"{cf.Property}{cf.Op switch
        {
            CompareOp.GreaterThan => ">",
            CompareOp.LessThan => "<",
            CompareOp.Equals => "==",
            CompareOp.GreaterOrEqual => ">=",
            CompareOp.LessOrEqual => "<=",
            _ => cf.Op.ToString()
        }}{cf.Value}",
        ContainsAnyFilter caf => $"{caf.Property}:ca([{string.Join(", ", caf.Values.Select(v => $"'{v}'"))}])",
        InFilter inf => $"{inf.Property}:in([{string.Join(", ", inf.Values.Select(v => $"'{v}'"))}])",
        CollectionContainsFilter ccf => $"{ccf.Property}:contains('{ccf.Value}')",
        CollectionAnyFilter caf2 => $"{caf2.Property}:any({Format(caf2.ItemFilter)})",
        CollectionCountFilter ccntf => $"{ccntf.Property}:count(){ccntf.Op switch
        {
            CompareOp.GreaterThan => ">",
            CompareOp.LessThan => "<",
            CompareOp.Equals => "==",
            CompareOp.GreaterOrEqual => ">=",
            CompareOp.LessOrEqual => "<=",
            _ => ccntf.Op.ToString()
        }}{ccntf.Value}",
        AndFilter af => string.Join(", ", af.Conditions.Select(Format)),
        OrFilter orf => $"({string.Join(" || ", orf.Conditions.Select(Format))})",
        NotFilter nf => $"!({Format(nf.Inner)})",
        _ => filter.ToString() ?? ""
    };
}

/// <summary>Boolean property check: e.g., Public == true, Abstract == false.</summary>
public record PropertyFilter(string Property, bool Value) : FilterExpression;

/// <summary>String operation on a property: e.g., Name.StartsWith("Client").</summary>
public record StringOpFilter(string Property, StringOp Op, string Value) : FilterExpression;

/// <summary>Numeric comparison: e.g., Size > 1000.</summary>
public record ComparisonFilter(string Property, CompareOp Op, double Value) : FilterExpression;

/// <summary>Conjunction of multiple conditions (all must be true).</summary>
public record AndFilter(List<FilterExpression> Conditions) : FilterExpression;

/// <summary>Disjunction of multiple conditions (any must be true).</summary>
public record OrFilter(List<FilterExpression> Conditions) : FilterExpression;

/// <summary>Negation of an inner expression.</summary>
public record NotFilter(FilterExpression Inner) : FilterExpression;

/// <summary>Check if any value in a list is a substring of the property: e.g., Name:ca(['Get', 'Set']).</summary>
public record ContainsAnyFilter(string Property, List<string> Values) : FilterExpression;

/// <summary>Check if property value is in a list: e.g., Extension:in(['.cs', '.vb']).</summary>
public record InFilter(string Property, List<string> Values) : FilterExpression;

/// <summary>Check if a collection property contains a value: e.g., Keywords:contains('var').</summary>
public record CollectionContainsFilter(string Property, string Value) : FilterExpression;

/// <summary>Check if any item in a collection satisfies a filter: e.g., Methods:any(Public).</summary>
public record CollectionAnyFilter(string Property, FilterExpression ItemFilter) : FilterExpression;

/// <summary>Compare collection count: e.g., Parameters:count() > 3.</summary>
public record CollectionCountFilter(string Property, CompareOp Op, int Value) : FilterExpression;

/// <summary>String operations for <see cref="StringOpFilter"/>.</summary>
public enum StringOp
{
    StartsWith,
    EndsWith,
    Contains,
    Equals,
    Matches,
    Same
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
