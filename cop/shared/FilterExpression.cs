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
            StringOp.StartsWith => "startsWith",
            StringOp.EndsWith => "endsWith",
            StringOp.Contains => "contains",
            StringOp.Equals => "equals",
            StringOp.Matches => "matches",
            StringOp.Same => "sameAs",
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
        AndFilter af => string.Join(", ", af.Conditions.Select(Format)),
        OrFilter orf => $"({string.Join(" || ", orf.Conditions.Select(Format))})",
        NotFilter nf => $"!({Format(nf.Inner)})",
        _ => filter.ToString() ?? ""
    };
}

/// <summary>Boolean property check: e.g., <c>Public</c> (true) or <c>!Abstract</c> (false).</summary>
public record PropertyFilter(string Property, bool Value) : FilterExpression;

/// <summary>
/// String predicate on a property. All string comparisons are case-insensitive
/// except <see cref="StringOp.Matches"/> (regex).
/// <list type="table">
///   <listheader><term>Op</term><description>Cop syntax → meaning</description></listheader>
///   <item><term>eq</term><description><c>Name:eq('Foo')</c> — case-insensitive equality</description></item>
///   <item><term>sw</term><description><c>Name:sw('I')</c> — case-insensitive starts-with</description></item>
///   <item><term>ew</term><description><c>Name:ew('Client')</c> — case-insensitive ends-with</description></item>
///   <item><term>ct</term><description><c>Name:ct('task')</c> — case-insensitive substring (contains)</description></item>
///   <item><term>rx</term><description><c>Name:rx('^Foo$')</c> — case-sensitive regex match</description></item>
///   <item><term>sm</term><description><c>Name:sm('foo')</c> — case-sensitive exact equality (same)</description></item>
/// </list>
/// The <c>==</c> and <c>!=</c> operators also compile to case-insensitive equality.
/// </summary>
public record StringOpFilter(string Property, StringOp Op, string Value) : FilterExpression;

/// <summary>
/// Numeric comparison on a property: e.g., <c>Size > 1000</c>, <c>Line == 42</c>.
/// Supports <c>&gt;</c>, <c>&lt;</c>, <c>&gt;=</c>, <c>&lt;=</c>, and <c>==</c>.
/// </summary>
public record ComparisonFilter(string Property, CompareOp Op, double Value) : FilterExpression;

/// <summary>Conjunction — all conditions must be true (AND).</summary>
public record AndFilter(List<FilterExpression> Conditions) : FilterExpression;

/// <summary>Disjunction — any condition must be true (OR).</summary>
public record OrFilter(List<FilterExpression> Conditions) : FilterExpression;

/// <summary>Negation of an inner expression (NOT).</summary>
public record NotFilter(FilterExpression Inner) : FilterExpression;

/// <summary>
/// <c>ca</c> — "contains any". True if the property string contains any value in the list
/// as a substring (case-insensitive). E.g., <c>Name:ca(['Get', 'Set'])</c>.
/// </summary>
public record ContainsAnyFilter(string Property, List<string> Values) : FilterExpression;

/// <summary>
/// <c>in</c> — "in list". True if the property value equals any value in the list
/// (case-insensitive). E.g., <c>Extension:in(['.cs', '.vb'])</c>.
/// </summary>
public record InFilter(string Property, List<string> Values) : FilterExpression;

/// <summary>
/// <c>contains</c> — collection membership. True if a collection property (list of strings)
/// contains the given value. E.g., <c>Keywords:contains('var')</c>.
/// </summary>
public record CollectionContainsFilter(string Property, string Value) : FilterExpression;

/// <summary>
/// <c>any</c> — collection item predicate. True if any item in an object collection
/// satisfies the inner filter. E.g., <c>Methods:any(Public)</c>.
/// </summary>
public record CollectionAnyFilter(string Property, FilterExpression ItemFilter) : FilterExpression;

/// <summary>
/// Collection count comparison. E.g., <c>Parameters.Count > 3</c>
/// or an equivalent predicate on the collection length.
/// </summary>
public record CollectionCountFilter(string Property, CompareOp Op, int Value) : FilterExpression;

/// <summary>
/// String comparison operations for <see cref="StringOpFilter"/>.
/// All operations are case-insensitive except <see cref="Matches"/> (regex)
/// and <see cref="Same"/> (case-sensitive exact equality).
/// </summary>
public enum StringOp
{
    /// <summary><c>sw</c> — case-insensitive starts-with.</summary>
    StartsWith,
    /// <summary><c>ew</c> — case-insensitive ends-with.</summary>
    EndsWith,
    /// <summary><c>ct</c> — case-insensitive substring (contains).</summary>
    Contains,
    /// <summary><c>eq</c> — case-insensitive equality (also used for <c>==</c> and <c>!=</c>).</summary>
    Equals,
    /// <summary><c>rx</c> — case-sensitive regular expression match.</summary>
    Matches,
    /// <summary><c>sm</c> — case-sensitive exact equality (same).</summary>
    Same
}

/// <summary>
/// Numeric comparison operations for <see cref="ComparisonFilter"/>
/// and <see cref="CollectionCountFilter"/>.
/// </summary>
public enum CompareOp
{
    /// <summary><c>&gt;</c></summary>
    GreaterThan,
    /// <summary><c>&lt;</c></summary>
    LessThan,
    /// <summary><c>==</c></summary>
    Equals,
    /// <summary><c>&gt;=</c></summary>
    GreaterOrEqual,
    /// <summary><c>&lt;=</c></summary>
    LessOrEqual
}
