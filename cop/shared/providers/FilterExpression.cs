// Defines composable filters that providers can inspect and execute.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// Represents a filter condition that can be pushed down to a data provider.
/// Analogous to a LINQ expression tree node — providers can inspect and optimize.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PropertyFilter), "property")]
[JsonDerivedType(typeof(StringOpFilter), "string")]
[JsonDerivedType(typeof(ComparisonFilter), "comparison")]
[JsonDerivedType(typeof(AndFilter), "and")]
[JsonDerivedType(typeof(OrFilter), "or")]
[JsonDerivedType(typeof(NotFilter), "not")]
[JsonDerivedType(typeof(ContainsAnyFilter), "containsAny")]
[JsonDerivedType(typeof(InFilter), "in")]
[JsonDerivedType(typeof(CollectionContainsFilter), "collectionContains")]
[JsonDerivedType(typeof(CollectionAnyFilter), "collectionAny")]
[JsonDerivedType(typeof(CollectionCountFilter), "collectionCount")]
[JsonDerivedType(typeof(FlagsFilter), "flags")]
public abstract record FilterExpression
{
    /// <summary>Combines two expressions with AND.</summary>
    public static FilterExpression And(FilterExpression left, FilterExpression right)
    {
        // Flatten nested ANDs
        List<FilterExpression> conditions = new List<FilterExpression>();
        Flatten(left, conditions);
        Flatten(right, conditions);
        return new AndFilter(conditions);

        static void Flatten(FilterExpression expr, List<FilterExpression> list)
        {
            if (expr is AndFilter and)
            {
                list.AddRange(and.Conditions);
            }
            else
            {
                list.Add(expr);
            }
        }
    }

    /// <summary>Combines two expressions with OR.</summary>
    public static FilterExpression Or(FilterExpression left, FilterExpression right)
    {
        // Flatten nested ORs
        List<FilterExpression> conditions = new List<FilterExpression>();
        Flatten(left, conditions);
        Flatten(right, conditions);
        return new OrFilter(conditions);

        static void Flatten(FilterExpression expr, List<FilterExpression> list)
        {
            if (expr is OrFilter or)
            {
                list.AddRange(or.Conditions);
            }
            else
            {
                list.Add(expr);
            }
        }
    }

    /// <summary>Formats a filter expression as a human-readable string for diagnostics.</summary>
    public static string Format(FilterExpression filter) => filter switch
    {
        PropertyFilter pf => pf.Value ? pf.Property : $"!{pf.Property}",
        StringOpFilter sf => $"{sf.Property}:{sf.Op switch
        {
            StringOp.StartsWith => "startsWith",
            StringOp.EndsWith => "endsWith",
            StringOp.Contains => "contains",
            StringOp.Equals => "equals",
            StringOp.Matches => "matches",
            StringOp.Same => "sameAs",
            StringOp.ExactEquals => "equalsExact",
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
        ContainsAnyFilter caf => $"{caf.Property}:containsAny([{string.Join(", ", caf.Values.Select(v => $"'{v}'"))}])",
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
        FlagsFilter ff => $"{ff.Property}:{(ff.Op == FlagsOp.IsSet ? "isSet" : "isClear")}({ff.Value})",
        AndFilter af => string.Join(", ", af.Conditions.Select(Format)),
        OrFilter orf => $"({string.Join(" || ", orf.Conditions.Select(Format))})",
        NotFilter nf => $"!({Format(nf.Inner)})",
        _ => filter.ToString() ?? ""
    };
}
