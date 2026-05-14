using System.Text;

namespace Cop.Lang;

/// <summary>
/// Computes a canonical, order-independent cache key for collection queries.
/// Pure predicate filters (commutative) are sorted alphabetically so that
/// Types:Public:Abstract and Types:Abstract:Public produce the same fingerprint.
/// Non-commutative operations (select, text, function maps) act as barriers
/// that flush and sort the accumulated predicate group before appending.
/// </summary>
public static class QueryFingerprint
{
    private static readonly HashSet<string> BarrierFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Select", "text"
    };

    /// <summary>
    /// Computes a canonical fingerprint for a collection query.
    /// </summary>
    /// <param name="baseCollection">The root collection name (e.g., "Types").</param>
    /// <param name="filters">The filter chain expressions.</param>
    /// <param name="docPath">Document path for per-document collections, or null for globals.</param>
    /// <param name="functionNames">Names of user-defined functions (treated as non-commutative barriers).</param>
    /// <param name="pathOverride">Path override for path-scoped collection queries, or null for default.</param>
    public static string Compute(string baseCollection, List<Expression> filters, string? docPath, IReadOnlySet<string>? functionNames = null, string? pathOverride = null)
    {
        if (filters.Count == 0)
        {
            var key = docPath is null ? baseCollection : $"{baseCollection}@{docPath}";
            return pathOverride is null ? key : $"{key}#{pathOverride}";
        }

        var sb = new StringBuilder(baseCollection);
        var pendingPredicates = new List<string>();

        foreach (var filter in filters)
        {
            if (IsBarrier(filter, functionNames))
            {
                FlushPredicates(sb, pendingPredicates);
                // Built-in transforms (Select, Text) use '.' separator; user functions use ':'
                bool isTransform = filter is CallExpr { Target: null } fc2 && BarrierFunctions.Contains(fc2.Name);
                sb.Append(isTransform ? '.' : ':');
                sb.Append(Serialize(filter));
            }
            else
            {
                pendingPredicates.Add(Serialize(filter));
            }
        }

        FlushPredicates(sb, pendingPredicates);

        if (docPath is not null)
        {
            sb.Append('@');
            sb.Append(docPath);
        }

        if (pathOverride is not null)
        {
            sb.Append('#');
            sb.Append(pathOverride);
        }

        return sb.ToString();
    }

    private static bool IsBarrier(Expression filter, IReadOnlySet<string>? functionNames)
    {
        if (filter is CallExpr { Target: null } fc)
        {
            // Built-in barriers: select, text
            if (BarrierFunctions.Contains(fc.Name)) return true;
            // User-defined function maps are also barriers
            if (functionNames is not null && functionNames.Contains(fc.Name)) return true;
        }
        return false;
    }

    private static void FlushPredicates(StringBuilder sb, List<string> predicates)
    {
        if (predicates.Count == 0) return;
        predicates.Sort(StringComparer.Ordinal);
        foreach (var p in predicates)
        {
            sb.Append(':');
            sb.Append(p);
        }
        predicates.Clear();
    }

    /// <summary>
    /// Serializes an expression to a canonical string representation.
    /// </summary>
    internal static string Serialize(Expression expr)
    {
        return expr switch
        {
            IdentifierExpr id => id.Name,
            NicExpr => "nic",
            LiteralExpr lit => SerializeLiteral(lit.Value),
            UnaryExpr un => $"{un.Operator}{Serialize(un.Operand)}",
            BinaryExpr bin => $"({Serialize(bin.Left)}{bin.Operator}{Serialize(bin.Right)})",
            MemberAccessExpr ma => $"{Serialize(ma.Target)}.{ma.Member}",
            CallExpr pc => SerializeCall(pc),
            ListLiteralExpr list => $"[{string.Join(",", list.Elements.Select(Serialize))}]",
            CollectionUnionExpr union => $"({string.Join("+", union.Elements.Select(Serialize))})",
            ObjectLiteralExpr obj => SerializeObject(obj),
            ConditionalExpr cond => $"({Serialize(cond.Condition)}?{Serialize(cond.TrueExpr)}|{Serialize(cond.FalseExpr)})",
            MatchExpr match => $"({Serialize(match.Discriminant)}?{string.Join("|", match.Arms.Select(a => $"{(a.Pattern is null ? "_" : Serialize(a.Pattern))}=>{Serialize(a.Result)}"))})",
            _ => expr.ToString() ?? "?"
        };
    }

    private static string SerializeLiteral(object value)
    {
        return value switch
        {
            string s => $"'{s}'",
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? "null"
        };
    }

    private static string SerializeCall(CallExpr c)
    {
        var args = c.Args.Count > 0
            ? $"({string.Join(",", c.Args.Select(Serialize))})"
            : "()";
        if (c.Target is not null)
        {
            var target = Serialize(c.Target);
            var prefix = c.Negated ? "!" : "";
            return $"{prefix}{target}.{c.Name}{args}";
        }
        return $"{c.Name}{args}";
    }

    private static string SerializeObject(ObjectLiteralExpr obj)
    {
        var fields = obj.Fields
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}:{Serialize(kv.Value)}");
        var prefix = obj.TypeName != null ? $"{obj.TypeName}" : "";
        return $"{prefix}{{{string.Join(",", fields)}}}";
    }
}
