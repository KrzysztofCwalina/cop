// A query predicate and its source definitions, projection, or method wrapper.

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
/// A query predicate and its source definitions, projection, or method wrapper.
/// </summary>
internal sealed record QueryStep(Expression Predicate, IReadOnlyList<FunctionDecl> Definitions,
    bool Negated = false, string? Property = null, string? Method = null)
{
    /// <summary>
    /// Applies this query transformation to a source and resolved predicate expression.
    /// </summary>
    public Expression Apply(Expression source, Expression predicate) => Method is not null
        ? new CallExpr(new MemberExpr(source, Method), [predicate])
        : new FilterExpr(Property is null ? source : new FilterExpr(source, new IdentifierExpr(Property)),
            predicate, Negated);
}
