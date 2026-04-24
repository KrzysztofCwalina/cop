using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Analyzes a cop filter chain and extracts pushdown-able conditions.
/// Splits filters into a pushdown prefix (passed to the provider) and
/// residual filters (applied locally by the interpreter).
///
/// Pushdown-able operations per property type:
///   bool:   == true, == false  (bare identifier or negated)
///   string: equals, startsWith, endsWith, contains, matches
///   int:    ==, !=, >, &lt;, >=, &lt;=
///
/// The extraction stops at "barrier" operations: Select, Text, user-defined
/// predicates that can't be inlined, function maps, or any filter that can't
/// be represented as a simple property check.
/// </summary>
public static class FilterHintExtractor
{
    /// <summary>
    /// Extracts pushdown-able filter hints from a filter chain.
    /// Returns the extracted FilterExpression (or null if none) and the
    /// index of the first non-pushdown filter (residual start).
    /// </summary>
    public static (FilterExpression? Hints, int ResidualStartIndex) Extract(
        List<Expression> filters,
        TypeDescriptor? itemType,
        HashSet<string>? predicateNames = null,
        Dictionary<string, List<PredicateDefinition>>? predicateDefs = null)
    {
        if (itemType is null || filters.Count == 0)
            return (null, 0);

        var hints = new List<FilterExpression>();
        int i = 0;

        for (; i < filters.Count; i++)
        {
            var hint = TryExtract(filters[i], itemType, predicateNames, predicateDefs);
            if (hint is null)
                break; // Hit a barrier — stop extracting

            hints.Add(hint);
        }

        if (hints.Count == 0)
            return (null, 0);

        var combined = hints.Count == 1 ? hints[0] : new AndFilter(hints);
        return (combined, i);
    }

    private static FilterExpression? TryExtract(
        Expression filter,
        TypeDescriptor itemType,
        HashSet<string>? predicateNames,
        Dictionary<string, List<PredicateDefinition>>? predicateDefs)
    {
        switch (filter)
        {
            // Bare identifier — bool property or user predicate to inline
            case IdentifierExpr id:
                return TryExtractIdentifier(id.Name, negated: false, itemType, predicateNames, predicateDefs);

            // Negated identifier — !Empty, !predicateName
            case UnaryExpr { Operator: "!" or "not", Operand: IdentifierExpr id }:
                return TryExtractIdentifier(id.Name, negated: true, itemType, predicateNames, predicateDefs);

            // Predicate call — Name:sw('Client'), Size:gt(100), Extension:eq('.cs')
            // or collection predicate — Keywords:contains('var')
            case PredicateCallExpr pc when pc.Target is IdentifierExpr prop:
                return TryExtractPredicateCall(prop.Name, pc.Name, pc.Args, itemType);

            // Binary expression — Depth < 3, Size > 1000, Extension == '.cs'
            case BinaryExpr bin:
                return TryExtractBinary(bin, itemType, paramName: null);

            default:
                return null;
        }
    }

    private static FilterExpression? TryExtractIdentifier(
        string name, bool negated,
        TypeDescriptor itemType,
        HashSet<string>? predicateNames,
        Dictionary<string, List<PredicateDefinition>>? predicateDefs)
    {
        // If it's a known user predicate, try to inline its body
        if (predicateNames is not null && predicateNames.Contains(name))
        {
            if (predicateDefs is not null && predicateDefs.TryGetValue(name, out var defs))
            {
                var inlined = TryInlinePredicateBody(defs[0], itemType);
                if (inlined is not null)
                    return negated ? new NotFilter(inlined) : inlined;
            }
            return null; // Can't inline — barrier
        }

        // Check if it's a boolean property on the type
        var prop = itemType.GetProperty(name);
        if (prop is not null && prop.TypeName == "bool")
            return new PropertyFilter(name, !negated);

        return null;
    }

    /// <summary>
    /// Tries to extract a pushdown FilterExpression from a user predicate's body.
    /// Handles: bool properties, negated bools, string ops, comparisons, equality,
    /// and AND/OR combinations of these.
    /// </summary>
    private static FilterExpression? TryInlinePredicateBody(
        PredicateDefinition predicate,
        TypeDescriptor itemType)
    {
        var paramName = predicate.ParameterType;
        return TryExtractFromBody(predicate.Body, itemType, paramName);
    }

    private static FilterExpression? TryExtractFromBody(Expression body, TypeDescriptor itemType, string? paramName)
    {
        switch (body)
        {
            // Param.Prop → boolean property check
            case MemberAccessExpr ma when IsParamAccess(ma.Target, paramName):
                return TryExtractBoolProperty(ma.Member, negated: false, itemType);

            // !Param.Prop → negated boolean
            case UnaryExpr { Operator: "!" or "not", Operand: MemberAccessExpr ma }
                when IsParamAccess(ma.Target, paramName):
                return TryExtractBoolProperty(ma.Member, negated: true, itemType);

            // Param.Prop:predicate('value') — string/numeric or collection operation
            case PredicateCallExpr pc
                when pc.Target is MemberAccessExpr ma
                && IsParamAccess(ma.Target, paramName):
                return TryExtractPredicateCall(ma.Member, pc.Name, pc.Args, itemType);

            // Binary — Param.Prop == 'value', Param.Prop > 3, or &&/|| combinations
            case BinaryExpr bin:
                return TryExtractBinary(bin, itemType, paramName);

            default:
                return null;
        }
    }

    /// <summary>
    /// Extracts from binary expressions: comparisons, equality, and logical AND/OR.
    /// </summary>
    private static FilterExpression? TryExtractBinary(BinaryExpr bin, TypeDescriptor itemType, string? paramName)
    {
        // Logical AND — both sides must be pushdown-able
        if (bin.Operator == "&&")
        {
            var left = TryExtractFromBody(bin.Left, itemType, paramName);
            var right = TryExtractFromBody(bin.Right, itemType, paramName);
            if (left is not null && right is not null)
                return FilterExpression.And(left, right);
            return null;
        }

        // Logical OR — both sides must be pushdown-able
        if (bin.Operator == "||")
        {
            var left = TryExtractFromBody(bin.Left, itemType, paramName);
            var right = TryExtractFromBody(bin.Right, itemType, paramName);
            if (left is not null && right is not null)
                return FilterExpression.Or(left, right);
            return null;
        }

        // Comparison/equality: Prop op Literal  or  Literal op Prop
        var (propName, op, literal) = ExtractPropertyOpLiteral(bin, paramName);
        if (propName is null || literal is null)
            return null;

        var prop = itemType.GetProperty(propName);
        if (prop is null)
            return null;

        return prop.TypeName switch
        {
            // String: == and != only
            "string" when literal is string sv => op switch
            {
                "==" => new StringOpFilter(propName, StringOp.Equals, sv),
                "!=" => new NotFilter(new StringOpFilter(propName, StringOp.Equals, sv)),
                _ => null // >, < etc. don't apply to strings
            },

            // Int: all comparison ops
            "int" when AsDouble(literal) is double num => op switch
            {
                "==" => new ComparisonFilter(propName, CompareOp.Equals, num),
                "!=" => new NotFilter(new ComparisonFilter(propName, CompareOp.Equals, num)),
                ">" => new ComparisonFilter(propName, CompareOp.GreaterThan, num),
                "<" => new ComparisonFilter(propName, CompareOp.LessThan, num),
                ">=" => new ComparisonFilter(propName, CompareOp.GreaterOrEqual, num),
                "<=" => new ComparisonFilter(propName, CompareOp.LessOrEqual, num),
                _ => null
            },

            // Bool: == true / == false only
            "bool" when literal is bool bv => op switch
            {
                "==" => new PropertyFilter(propName, bv),
                "!=" => new PropertyFilter(propName, !bv),
                _ => null
            },

            _ => null
        };
    }

    /// <summary>
    /// Extracts (propertyName, operator, literalValue) from a binary expression.
    /// Handles both "Prop op Literal" and "Literal op Prop" orderings,
    /// and both bare identifiers and Param.Prop member access.
    /// </summary>
    private static (string? PropName, string Op, object? Literal) ExtractPropertyOpLiteral(BinaryExpr bin, string? paramName)
    {
        var propName = ExtractPropertyName(bin.Left, paramName);
        if (propName is not null && bin.Right is LiteralExpr lit)
            return (propName, bin.Operator, lit.Value);

        // Reversed: Literal op Prop — flip the operator
        propName = ExtractPropertyName(bin.Right, paramName);
        if (propName is not null && bin.Left is LiteralExpr litLeft)
        {
            var flipped = bin.Operator switch
            {
                ">" => "<",
                "<" => ">",
                ">=" => "<=",
                "<=" => ">=",
                _ => bin.Operator // ==, != are symmetric
            };
            return (propName, flipped, litLeft.Value);
        }

        return (null, bin.Operator, null);
    }

    private static string? ExtractPropertyName(Expression expr, string? paramName) => expr switch
    {
        // Bare identifier: Depth, Name, etc. (used in direct filter chain)
        IdentifierExpr id when paramName is null => id.Name,
        // Param.Prop: DiskFile.Depth, Folder.Empty, etc. (used in predicate body)
        MemberAccessExpr ma when IsParamAccess(ma.Target, paramName) => ma.Member,
        _ => null
    };

    private static bool IsParamAccess(Expression target, string? paramName) =>
        paramName is not null && target is IdentifierExpr id && id.Name == paramName;

    private static FilterExpression? TryExtractBoolProperty(string name, bool negated, TypeDescriptor itemType)
    {
        var prop = itemType.GetProperty(name);
        if (prop is null || prop.TypeName != "bool") return null;
        return new PropertyFilter(name, !negated);
    }

    /// <summary>
    /// Dispatches a predicate call to either scalar or collection predicate extraction.
    /// </summary>
    private static FilterExpression? TryExtractPredicateCall(
        string propertyName, string predicateName, List<Expression> args,
        TypeDescriptor itemType)
    {
        var prop = itemType.GetProperty(propertyName);
        if (prop is null) return null;

        if (prop.IsCollection)
            return TryExtractCollectionPredicate(propertyName, predicateName, args, itemType);

        if (IsBuiltinPredicate(predicateName))
            return TryExtractPredicateOp(propertyName, predicateName, args, itemType);

        return null;
    }

    /// <summary>
    /// Extracts a pushdown filter from a collection predicate like Keywords:contains('var').
    /// </summary>
    private static FilterExpression? TryExtractCollectionPredicate(
        string propertyName, string predicateName, List<Expression> args,
        TypeDescriptor itemType)
    {
        switch (predicateName)
        {
            case "contains" when args.Count == 1 && args[0] is LiteralExpr lit && lit.Value is string sv:
                return new CollectionContainsFilter(propertyName, sv);
            default:
                return null;
        }
    }

    /// <summary>
    /// Extracts a pushdown filter from a predicate call like Extension:eq('.cs') or Size:gt(100).
    /// Dispatches by property type from the schema.
    /// </summary>
    private static FilterExpression? TryExtractPredicateOp(
        string propertyName, string predicateName, List<Expression> args,
        TypeDescriptor itemType)
    {
        var prop = itemType.GetProperty(propertyName);
        if (prop is null || prop.IsCollection) return null;

        // List-argument predicates: ca (containsAny), in
        if (predicateName is "ca" or "in" && args.Count == 1 && args[0] is ListLiteralExpr listLit)
        {
            var values = new List<string>();
            foreach (var elem in listLit.Elements)
            {
                if (elem is not LiteralExpr lit || lit.Value is not string sv) return null;
                values.Add(sv);
            }
            if (prop.TypeName == "string")
            {
                return predicateName switch
                {
                    "ca" => new ContainsAnyFilter(propertyName, values),
                    "in" => new InFilter(propertyName, values),
                    _ => null
                };
            }
            return null;
        }

        // Single-argument predicates
        if (args.Count != 1 || args[0] is not LiteralExpr literal)
            return null;

        return prop.TypeName switch
        {
            "string" when literal.Value is string sv => TryStringPredicate(propertyName, predicateName, sv),
            "int" when AsDouble(literal.Value) is double num => TryNumericPredicate(propertyName, predicateName, num),
            _ => null
        };
    }

    private static FilterExpression? TryStringPredicate(string prop, string predName, string value) => predName switch
    {
        "eq" => new StringOpFilter(prop, StringOp.Equals, value),
        "ne" => new NotFilter(new StringOpFilter(prop, StringOp.Equals, value)),
        "sw" => new StringOpFilter(prop, StringOp.StartsWith, value),
        "ew" => new StringOpFilter(prop, StringOp.EndsWith, value),
        "ct" => new StringOpFilter(prop, StringOp.Contains, value),
        "rx" => new StringOpFilter(prop, StringOp.Matches, value),
        "sm" => new StringOpFilter(prop, StringOp.Same, value),
        _ => null
    };

    private static FilterExpression? TryNumericPredicate(string prop, string predName, double value) => predName switch
    {
        "eq" => new ComparisonFilter(prop, CompareOp.Equals, value),
        "ne" => new NotFilter(new ComparisonFilter(prop, CompareOp.Equals, value)),
        "gt" => new ComparisonFilter(prop, CompareOp.GreaterThan, value),
        "lt" => new ComparisonFilter(prop, CompareOp.LessThan, value),
        "ge" => new ComparisonFilter(prop, CompareOp.GreaterOrEqual, value),
        "le" => new ComparisonFilter(prop, CompareOp.LessOrEqual, value),
        _ => null
    };

    private static bool IsBuiltinPredicate(string name) =>
        name is "eq" or "ne"
            or "sw" or "ew"
            or "ct" or "rx"
            or "ca" or "sm"
            or "in" or "empty"
            or "gt" or "lt"
            or "ge" or "le";

    private static double? AsDouble(object? v) => v switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        _ => null
    };
}
