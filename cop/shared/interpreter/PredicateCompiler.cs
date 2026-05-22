using Cop.Core;
using AstExpr = Cop.Lang.Ast.Expression;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Compiles Cop predicate AST expressions into FilterExpression trees for provider pushdown.
/// Returns null when the predicate cannot be compiled (requires runtime evaluation).
/// </summary>
public static class PredicateCompiler
{
    // Known string operation names (long and short forms)
    private static readonly HashSet<string> StringOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "startsWith", "sw", "endsWith", "ew", "contains", "ct",
        "equals", "eq", "matches", "rx", "same", "sm"
    };

    // Known comparison operation names
    private static readonly HashSet<string> ComparisonOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "greaterThan", "gt", "lessThan", "lt", "greaterOrEqual", "ge", "lessOrEqual", "le"
    };

    // Known collection-level functions that should NOT be treated as property names
    private static readonly HashSet<string> CollectionFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "any", "all", "none", "count", "where", "select", "first", "last",
        "single", "orderBy", "orderByDescending", "distinct", "take", "skip",
        "groupBy", "reduce", "sum", "min", "max", "average", "concat", "push",
        "pop", "enqueue", "empty", "text", "print", "debug", "save", "read",
        "provider", "source", "sink", "assert", "fail", "error"
    };

    /// <summary>
    /// Attempts to compile a predicate expression (from a FilterExpr) into a FilterExpression.
    /// Returns null if the predicate is not compilable to a pushdown filter.
    /// </summary>
    public static FilterExpression? TryCompile(AstExpr predicate, bool negated = false, string? propertyContext = null)
    {
        var result = CompileCore(predicate, propertyContext);
        if (result is null) return null;
        return negated ? new NotFilter(result) : result;
    }

    /// <summary>
    /// Attempts to extract a property name from a predicate expression.
    /// Used to detect compound patterns: queryable:PropertyName:StringOp(value).
    /// Returns the property name if the expression is a simple property access, null otherwise.
    /// </summary>
    public static string? TryExtractPropertyAccess(AstExpr predicate)
    {
        if (predicate is Ast.IdentifierExpr id && !CollectionFunctions.Contains(id.Name))
            return id.Name;

        if (predicate is Ast.MemberExpr { Object: Ast.IdentifierExpr } mem)
            return mem.Member;

        return null;
    }

    private static FilterExpression? CompileCore(AstExpr predicate, string? propertyContext)
    {
        switch (predicate)
        {
            // Bare identifier: configs:enabled → PropertyFilter("enabled", true)
            case Ast.IdentifierExpr id when !CollectionFunctions.Contains(id.Name):
                return new PropertyFilter(id.Name, true);

            // Call expression: :startsWith('A') or :gt(100)
            case Ast.CallExpr { Callee: Ast.IdentifierExpr callee } call:
                return CompileCall(callee.Name, call.Args, propertyContext);

            // Binary AND: pred1 && pred2
            case Ast.BinaryExpr { Op: BinaryOp.And } bin:
                var leftFilter = CompileCore(bin.Left, propertyContext);
                var rightFilter = CompileCore(bin.Right, propertyContext);
                if (leftFilter is null || rightFilter is null) return null;
                return FilterExpression.And(leftFilter, rightFilter);

            // Binary OR: pred1 || pred2
            case Ast.BinaryExpr { Op: BinaryOp.Or } bin2:
                var leftOr = CompileCore(bin2.Left, propertyContext);
                var rightOr = CompileCore(bin2.Right, propertyContext);
                if (leftOr is null || rightOr is null) return null;
                return FilterExpression.Or(leftOr, rightOr);

            // Unary NOT: !pred
            case Ast.UnaryExpr { Op: UnaryOp.Not } un:
                var inner = CompileCore(un.Operand, propertyContext);
                if (inner is null) return null;
                return new NotFilter(inner);

            // Nested filter: Property:op(value) — compound pattern
            case Ast.FilterExpr { Collection: Ast.IdentifierExpr propId } filt
                when !CollectionFunctions.Contains(propId.Name):
                var compiled = CompileCore(filt.Predicate, propId.Name);
                if (compiled is null) return null;
                return filt.Negated ? new NotFilter(compiled) : compiled;

            default:
                return null;
        }
    }

    private static FilterExpression? CompileCall(string methodName, List<AstExpr> args, string? propertyContext)
    {
        // Without property context, we can't compile a call (no property to filter on)
        if (propertyContext is null) return null;
        if (args.Count < 1) return null;

        // equals/eq: polymorphic — string or numeric
        if (methodName is "equals" or "eq")
        {
            if (args[0] is Ast.LiteralExpr { Value: string sv })
                return new StringOpFilter(propertyContext, StringOp.Equals, sv);
            double? nv = args[0] switch
            {
                Ast.LiteralExpr { Value: int i } => i,
                Ast.LiteralExpr { Value: double d } => d,
                Ast.LiteralExpr { Value: long l } => l,
                _ => null
            };
            if (nv is not null)
                return new ComparisonFilter(propertyContext, CompareOp.Equals, nv.Value);
            return null;
        }

        // String operations: property:startsWith('value')
        if (StringOps.Contains(methodName))
        {
            if (args[0] is not Ast.LiteralExpr { Value: string strVal })
                return null;
            var op = NormalizeStringOp(methodName);
            if (op is null) return null;
            return new StringOpFilter(propertyContext, op.Value, strVal);
        }

        // Comparison operations: property:gt(100)
        if (ComparisonOps.Contains(methodName))
        {
            double? numVal = args[0] switch
            {
                Ast.LiteralExpr { Value: int i } => i,
                Ast.LiteralExpr { Value: double d } => d,
                Ast.LiteralExpr { Value: long l } => l,
                _ => null
            };
            if (numVal is null) return null;
            var op = NormalizeCompareOp(methodName);
            if (op is null) return null;
            return new ComparisonFilter(propertyContext, op.Value, numVal.Value);
        }

        return null;
    }

    private static StringOp? NormalizeStringOp(string name) => name.ToLowerInvariant() switch
    {
        "startswith" or "sw" => StringOp.StartsWith,
        "endswith" or "ew" => StringOp.EndsWith,
        "contains" or "ct" => StringOp.Contains,
        "equals" or "eq" => StringOp.Equals,
        "matches" or "rx" => StringOp.Matches,
        "same" or "sm" => StringOp.Same,
        _ => null
    };

    private static CompareOp? NormalizeCompareOp(string name) => name.ToLowerInvariant() switch
    {
        "greaterthan" or "gt" => CompareOp.GreaterThan,
        "lessthan" or "lt" => CompareOp.LessThan,
        "greaterorequal" or "ge" => CompareOp.GreaterOrEqual,
        "lessorequal" or "le" => CompareOp.LessOrEqual,
        _ => null
    };
}
