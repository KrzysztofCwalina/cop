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
    // The name sets below are derived from the single IntrinsicRegistry so provider pushdown can
    // never drift from the language's actual built-ins. (The name -> StringOp/CompareOp mapping is a
    // pushdown-specific concern and stays local in NormalizeStringOp / NormalizeCompareOp.)
    private static readonly HashSet<string> StringOps =
        new(IntrinsicRegistry.NameSet(o => o.Kind == Cop.Lang.IntrinsicKind.StringPredicate && o.Pushable),
            StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ComparisonOps =
        new(IntrinsicRegistry.NameSet(o => o.Kind == Cop.Lang.IntrinsicKind.NumericPredicate && o.Pushable),
            StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CollectionFunctions = IntrinsicRegistry.CollectionFunctionNames();

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

    /// <summary>Expands unambiguous, pure predicate bodies; unsupported expressions stay local.</summary>
    public static FilterExpression? TryCompileBound(AstExpr predicate, Environment environment, string? itemType = null)
    {
        var expanding = new HashSet<string>(StringComparer.Ordinal);
        return Expand(predicate, environment, null);

        FilterExpression? Expand(AstExpr expression, Environment scope, string? parameter)
        {
            if (expression is Ast.IdentifierExpr id && scope.TryLookup(id.Name, out _))
            {
                var function = FindPredicate(id.Name, scope, itemType);
                if (function is null || !expanding.Add(id.Name))
                    return null;
                var result = Expand(((ExpressionBody)function.Declaration.Body).Expr,
                    function.Closure, function.Declaration.Params[0].Name);
                expanding.Remove(id.Name);
                return result;
            }

            if (parameter is null) return null;
            if (expression is Ast.BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } logical)
            {
                var left = Expand(logical.Left, scope, parameter);
                var right = Expand(logical.Right, scope, parameter);
                if (left is null || right is null) return null;
                return logical.Op == BinaryOp.And ? FilterExpression.And(left, right) : FilterExpression.Or(left, right);
            }
            if (expression is Ast.UnaryExpr { Op: UnaryOp.Not } not)
                return Expand(not.Operand, scope, parameter) is { } inner ? new NotFilter(inner) : null;
            if (expression is MemberExpr member && Property(member, parameter) is { } boolProperty)
                return new PropertyFilter(boolProperty, true);
            if (expression is FilterExpr filter && Property(filter.Collection, parameter) is { } property)
            {
                var compiled = TryCompile(filter.Predicate, filter.Negated, property);
                return compiled;
            }
            if (expression is Ast.BinaryExpr comparison && Property(comparison.Left, parameter) is { } name
                && comparison.Right is Ast.LiteralExpr literal)
            {
                if (literal.Value is bool boolean && comparison.Op is BinaryOp.Equal or BinaryOp.NotEqual)
                {
                    var equal = new PropertyFilter(name, boolean);
                    return comparison.Op == BinaryOp.Equal ? equal : new NotFilter(equal);
                }
                if (literal.Value is string text && comparison.Op is BinaryOp.Equal or BinaryOp.NotEqual)
                {
                    var equal = new StringOpFilter(name, StringOp.ExactEquals, text);
                    return comparison.Op == BinaryOp.Equal ? equal : new NotFilter(equal);
                }
                double? number = literal.Value switch { int i => i, long l => l, double d => d, _ => null };
                CompareOp? op = comparison.Op switch
                {
                    BinaryOp.Equal or BinaryOp.NotEqual => CompareOp.Equals,
                    BinaryOp.GreaterThan => CompareOp.GreaterThan,
                    BinaryOp.GreaterOrEqual => CompareOp.GreaterOrEqual,
                    BinaryOp.LessThan => CompareOp.LessThan,
                    BinaryOp.LessOrEqual => CompareOp.LessOrEqual,
                    _ => null
                };
                if (number is not null && op is not null)
                {
                    var numeric = new ComparisonFilter(name, op.Value, number.Value);
                    return comparison.Op == BinaryOp.NotEqual ? new NotFilter(numeric) : numeric;
                }
            }
            return null;
        }

        static string? Property(AstExpr expression, string parameter) =>
            expression is MemberExpr { Object: Ast.IdentifierExpr root } member
                && (root.Name == parameter || string.Equals(root.Name, parameter, StringComparison.OrdinalIgnoreCase))
                ? member.Member : null;
    }

    internal static CopFunction? FindPredicate(string name, Environment scope, string? itemType)
    {
        if (!scope.TryLookup(name, out var binding)) return null;
        var functions = binding switch
        {
            CopFunction function => new[] { function },
            CopFunctionGroup group => group.Functions.ToArray(),
            _ => []
        };
        var candidates = functions.Where(f => f.Declaration.IsPredicate && f.Arity == 1
            && f.Declaration.Guard is null && f.Declaration.Params[0].Type?.Constraint is null
            && f.Declaration.Body is ExpressionBody).ToArray();
        var exact = candidates.Where(f => f.Declaration.Params[0].Type?.Name == itemType).ToArray();
        if (exact.Length == 1) candidates = exact;
        if (candidates.Length != 1 || functions.Length > 1 && exact.Length != 1) return null;
        var selected = candidates[0];
        return itemType is not null && selected.Declaration.Params[0].Type is { } type && type.Name != itemType
            ? null : selected;
    }

    public static FilterExpression? TryCompileElement(AstExpr expression, Environment environment, string? itemType)
    {
        var parameter = new Parameter("item", null);
        if (expression is LambdaExpr { Params.Count: 1 } lambda)
        {
            parameter = lambda.Params[0];
            expression = lambda.Body;
        }
        var scope = environment.Extend();
        scope.Define("$query", new CopFunction(new FunctionDecl("$query", [parameter], null,
            new ExpressionBody(expression), IsPredicate: true), environment));
        return TryCompileBound(new Ast.IdentifierExpr("$query"), scope, itemType);
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

        // Membership test: property:in([...])
        if (methodName == "in" && args.Count == 1 && args[0] is Ast.ListExpr listLit)
        {
            var values = new List<string>();
            foreach (var elem in listLit.Elements)
            {
                if (elem is Ast.LiteralExpr { Value: string sv })
                    values.Add(sv);
                else
                    return null;
            }
            return new InFilter(propertyContext, values);
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
