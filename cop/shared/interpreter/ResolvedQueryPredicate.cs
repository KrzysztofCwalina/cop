// A resolved query predicate with its evaluation environment and definitions.

namespace Cop.Lang.Interpreter;

using Cop.Core;
using Cop.Lang.Ast;
using Expression = Cop.Lang.Ast.Expression;
using IdentifierExpr = Cop.Lang.Ast.IdentifierExpr;
using LiteralExpr = Cop.Lang.Ast.LiteralExpr;
using BinaryExpr = Cop.Lang.Ast.BinaryExpr;
using UnaryExpr = Cop.Lang.Ast.UnaryExpr;
using CallExpr = Cop.Lang.Ast.CallExpr;

/// <summary>
/// A resolved query predicate with its evaluation environment and definitions.
/// </summary>
internal sealed record ResolvedQueryPredicate(Expression Expression, Environment Environment,
    IReadOnlyList<FunctionDecl> Definitions)
{
    /// <summary>
    /// Captures referenced predicate bodies and literal values in a query scope.
    /// </summary>
    public static ResolvedQueryPredicate Resolve(Expression expression, Environment environment, string? itemType,
        QueryPredicateContext context = QueryPredicateContext.Collection)
    {
        List<FunctionDecl> definitions = new List<FunctionDecl>();
        Dictionary<CopFunction, string> names = new Dictionary<CopFunction, string>();
        Environment scope = environment.Extend();

        Expression ResolveExpression(Expression node, Environment closure, IEnumerable<string>? bound = null) =>
            QuerySource.Rewrite(node, id =>
            {
                if (TryLiteral(id, closure, new HashSet<CopThunk>()) is { } literal)
                {
                    return literal;
                }

                if (PredicateCompiler.FindPredicate(id.Name, closure, itemType) is not { } function)
                {
                    return id;
                }

                if (names.TryGetValue(function, out string? existingName))
                {
                    return id with { Name = existingName };
                }

                string name = id.Name;
                int suffix = 2;
                while (names.Values.Contains(name, StringComparer.Ordinal))
                {
                    name = id.Name + "__" + suffix++;
                }

                names.Add(function, name);
                FunctionDecl declaration = function.Declaration;
                Expression body = ResolveExpression(((ExpressionBody)declaration.Body).Expr, function.Closure,
                    declaration.Params.Select(p => p.Name));
                FunctionDecl resolved = declaration with { Name = name, Body = new ExpressionBody(body) };
                definitions.Add(resolved);
                scope.Define(name, new CopFunction(resolved, scope));
                return id with { Name = name };
            }, bound, node => node switch
            {
                UnaryExpr { Op: UnaryOp.Negate, Operand: LiteralExpr { Value: int integer } } => new LiteralExpr(-integer),
                UnaryExpr { Op: UnaryOp.Negate, Operand: LiteralExpr { Value: double number } } => new LiteralExpr(-number),
                _ => node
            });

        Expression resolvedExpression = ResolveExpression(expression, environment,
            context == QueryPredicateContext.Element ? ["item"] : []);
        return new(resolvedExpression, scope, definitions);
    }

    private static LiteralExpr? TryLiteral(IdentifierExpr identifier, Environment environment, HashSet<CopThunk> expanding)
    {
        if (!environment.TryLookup(identifier.Name, out CopValue? value))
        {
            return null;
        }

        if (value is CopThunk thunk)
        {
            if (!expanding.Add(thunk))
            {
                return null;
            }

            if (thunk.ForcedValue is { } forced)
            {
                value = forced;
            }
            else if (thunk.ConstantExpression is LiteralExpr literal)
            {
                return literal;
            }
            else if (thunk.ConstantExpression is IdentifierExpr alias && thunk.ConstantEnvironment is { } closure)
            {
                return TryLiteral(alias, closure, expanding);
            }
            else
            {
                return null;
            }
        }
        return value switch
        {
            CopString text => new LiteralExpr(text.Value),
            CopInt integer => new LiteralExpr(integer.Value),
            CopNumber number when double.IsFinite(number.Value) => new LiteralExpr(number.Value),
            CopBool boolean => new LiteralExpr(boolean.IsTrue),
            CopNull => new LiteralExpr(null),
            _ => null
        };
    }
}
