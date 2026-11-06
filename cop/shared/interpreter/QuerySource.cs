// Source provenance is deliberately separate from ProviderQuery and its wire/cache
// identity.

namespace Cop.Lang.Interpreter;

using Cop.Core;
using Cop.Lang.Ast;
using Expression = Cop.Lang.Ast.Expression;
using IdentifierExpr = Cop.Lang.Ast.IdentifierExpr;
using LiteralExpr = Cop.Lang.Ast.LiteralExpr;
using BinaryExpr = Cop.Lang.Ast.BinaryExpr;
using UnaryExpr = Cop.Lang.Ast.UnaryExpr;
using CallExpr = Cop.Lang.Ast.CallExpr;

/// <summary>Source provenance is deliberately separate from ProviderQuery and its wire/cache identity.</summary>
internal sealed class QuerySource
{
    private readonly Expression _root;
    private readonly IReadOnlyList<FunctionDecl> _rootDefinitions;
    private readonly (string Description, Action<string>? Log, IReadOnlySet<string> Roots) _context;
    /// <summary>
    /// The ordered query transformations retained for source reconstruction.
    /// </summary>
    public IReadOnlyList<QueryStep> Steps { get; }

    public QuerySource(string collection, string context, Action<string>? log = null)
        : this(new IdentifierExpr(collection), [], [],
            (context, log, new HashSet<string> { collection })) { }

    private QuerySource(Expression root, IReadOnlyList<FunctionDecl> definitions, IReadOnlyList<QueryStep> steps,
        (string Description, Action<string>? Log, IReadOnlySet<string> Roots) context)
    {
        _root = root;
        _rootDefinitions = definitions;
        Steps = steps;
        _context = context;
    }

    /// <summary>
    /// Returns provenance extended with the supplied query transformations.
    /// </summary>
    public QuerySource Append(QueryStep step) => Append([step]);
    /// <summary>
    /// Returns provenance extended with the supplied query transformations.
    /// </summary>
    public QuerySource Append(IEnumerable<QueryStep> steps) =>
        new(_root, _rootDefinitions, Steps.Concat(steps).ToArray(), _context);
    /// <summary>
    /// Returns the same query provenance with a replacement diagnostic sink.
    /// </summary>
    public QuerySource WithLog(Action<string>? log) =>
        new(_root, _rootDefinitions, Steps, (_context.Description, log, _context.Roots));
    /// <summary>
    /// Replaces local steps with a portable filter step when it can be represented.
    /// </summary>
    public QuerySource? ForFilter(FilterExpression filter) => FromFilter(filter) is { } step
        ? new(_root, _rootDefinitions, [step], _context) : null;

    /// <summary>
    /// Converts a provider filter into a portable query step, or returns null.
    /// </summary>
    public static QueryStep? FromFilter(FilterExpression filter)
    {
        Expression? Convert(FilterExpression node) => node switch
        {
            PropertyFilter property => new BinaryExpr(Field(property.Property), BinaryOp.Equal, new LiteralExpr(property.Value)),
            StringOpFilter text when text.Op == StringOp.ExactEquals =>
                new BinaryExpr(Field(text.Property), BinaryOp.Equal, new LiteralExpr(text.Value)),
            StringOpFilter { Op: StringOp.Same } => null,
            StringOpFilter text => new FilterExpr(Field(text.Property), new CallExpr(new IdentifierExpr(text.Op switch
            {
                StringOp.StartsWith => "startsWith",
                StringOp.EndsWith => "endsWith",
                StringOp.Contains => "contains",
                StringOp.Equals => "equals",
                StringOp.Matches => "matches",
                _ => throw new ArgumentOutOfRangeException(nameof(filter))
            }), [new LiteralExpr(text.Value)])),
            ComparisonFilter comparison => new BinaryExpr(Field(comparison.Property), comparison.Op switch
            {
                CompareOp.Equals => BinaryOp.Equal,
                CompareOp.GreaterThan => BinaryOp.GreaterThan,
                CompareOp.GreaterOrEqual => BinaryOp.GreaterOrEqual,
                CompareOp.LessThan => BinaryOp.LessThan,
                CompareOp.LessOrEqual => BinaryOp.LessOrEqual,
                _ => throw new ArgumentOutOfRangeException(nameof(filter))
            }, new LiteralExpr(comparison.Value)),
            NotFilter not when Convert(not.Inner) is { } operand => new UnaryExpr(UnaryOp.Not, operand),
            AndFilter and => Combine(and.Conditions, BinaryOp.And),
            OrFilter or => Combine(or.Conditions, BinaryOp.Or),
            InFilter values => new FilterExpr(Field(values.Property), new CallExpr(new IdentifierExpr("in"),
                [new ListExpr(values.Values.Select(value => (Expression)new LiteralExpr(value)).ToList())])),
            _ => null
        };
        Expression? Combine(IReadOnlyList<FilterExpression> conditions, BinaryOp op)
        {
            Expression? result = null;
            foreach (FilterExpression condition in conditions)
            {
                if (Convert(condition) is not { } expression)
                {
                    return null;
                }

                result = result is null ? expression : new BinaryExpr(result, op, expression);
            }
            return result ?? new LiteralExpr(op == BinaryOp.And);
        }
        static Expression Field(string name) => new MemberExpr(new IdentifierExpr("item"), name);
        return Convert(filter) is { } body
            ? new QueryStep(new LambdaExpr([new Parameter("item")], body), [], Method: "Where") : null;
    }

    /// <summary>
    /// The executable Cop program representing this query's current provenance.
    /// </summary>
    public string Program
    {
        get
        {
            (Expression? expression, IReadOnlyList<FunctionDecl>? definitions) = Build();
            return string.Join("\n", definitions.Select(QuerySourceWriter.Predicate)
                .Append("command MAIN = " + QuerySourceWriter.Render(expression)));
        }
    }

    /// <summary>
    /// Writes the provider query's reconstructed Cop source to its diagnostic sink.
    /// </summary>
    public void ReportProvider() => _context.Log?.Invoke(
        $"[query-cop] {_context.Description}\n{Program}");

    /// <summary>
    /// Tracks a local expression transformation and its reason without executing it.
    /// </summary>
    public QuerySource Local(Func<Expression, Expression> operation, string reason)
    {
        (Expression? expression, IReadOnlyList<FunctionDecl>? definitions) = Build();
        Expression local = operation(expression);
        // Local expressions may be arbitrary Cop, not the portable/pushable source subset.
        _context.Log?.Invoke($"[query-local] {_context.Description}: {reason}\n{(QuerySourceWriter.CanRender(local)
            ? QuerySourceWriter.Render(local) : $"Local {local.GetType().Name} at line {local.Line}; not a portable provider query")}");
        return new(local, definitions, [], _context);
    }

    /// <summary>
    /// Retrieves source provenance from a deferred collection value when available.
    /// </summary>
    public static QuerySource? Of(CopValue value) => value switch
    {
        CopQueryable query => query.Source,
        CopLazyCollection lazy => lazy.Source,
        CopList list => list.Source,
        CopThunk { ForcedValue: { } forced } => Of(forced),
        _ => null
    };

    /// <summary>
    /// Associates a locally transformed collection with its input's source provenance.
    /// </summary>
    public static CopValue TrackLocal(CopValue input, CopValue result, Func<Expression, Expression> operation, string reason)
    {
        if (Of(input) is not { } source)
        {
            return result;
        }

        QuerySource local = source.Local(operation, reason);
        switch (result)
        {
            case CopLazyCollection lazy: lazy.Source = local; break;
            case CopList list: list.Source = local; break;
        }
        return result;
    }

    /// <summary>
    /// Combines the portable provenance of two collections when both can be rendered.
    /// </summary>
    public static QuerySource? Concat(CopValue left, CopValue right)
    {
        QuerySource? leftSource = Of(left);
        QuerySource? rightSource = Of(right);
        QuerySource? context = leftSource ?? rightSource;
        if (context is null)
        {
            return null;
        }

        HashSet<string> roots = new HashSet<string>((leftSource?._context.Roots.AsEnumerable() ?? [])
            .Concat(rightSource?._context.Roots.AsEnumerable() ?? []));
        Builder builder = new Builder(roots);
        (Expression, IReadOnlyList<FunctionDecl>) a = leftSource?.Build() ?? (new IdentifierExpr("local"), (IReadOnlyList<FunctionDecl>)[]);
        (Expression, IReadOnlyList<FunctionDecl>) b = rightSource?.Build() ?? (new IdentifierExpr("local"), (IReadOnlyList<FunctionDecl>)[]);
        BinaryExpr expression = new BinaryExpr(builder.Import(a.Item1, a.Item2), BinaryOp.Add,
            builder.Import(b.Item1, b.Item2));
        return new(expression, builder.Definitions.ToArray(), [],
            (context._context.Description, context._context.Log, roots));
    }

    private (Expression, IReadOnlyList<FunctionDecl>) Build()
    {
        Builder builder = new Builder(_context.Roots);
        Expression expression = builder.Import(_root, _rootDefinitions);
        foreach (QueryStep step in Steps)
        {
            expression = step.Apply(expression, builder.Import(step.Predicate, step.Definitions));
        }

        return (expression, builder.Definitions);
    }

    private sealed class Builder(IReadOnlySet<string> roots)
    {
        /// <summary>
        /// The predicate declarations accumulated while importing query expressions.
        /// </summary>
        public List<FunctionDecl> Definitions { get; } = [];

        /// <summary>
        /// Imports an expression and its predicate definitions with collision-free
        /// names.
        /// </summary>
        public Expression Import(Expression expression, IReadOnlyList<FunctionDecl> definitions)
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (FunctionDecl definition in definitions)
            {
                Expression body = Rewrite(((ExpressionBody)definition.Body).Expr, id =>
                    names.TryGetValue(id.Name, out string? name) ? id with { Name = name } : id,
                    definition.Params.Select(p => p.Name));
                FunctionDecl candidate = definition with { Body = new ExpressionBody(body) };
                string name = definition.Name;
                int suffix = 2;
                while (roots.Contains(name) || Definitions.FirstOrDefault(d => d.Name == name) is { } existing
                    && QuerySourceWriter.Predicate(existing) != QuerySourceWriter.Predicate(candidate with { Name = name }))
                {
                    name = definition.Name + "__" + suffix++;
                }

                names[definition.Name] = name;
                if (Definitions.All(d => d.Name != name))
                {
                    Definitions.Add(candidate with { Name = name });
                }
            }
            return Rewrite(expression, id => names.TryGetValue(id.Name, out string? name) ? id with { Name = name } : id);
        }
    }

    internal static Expression Rewrite(Expression expression, Func<IdentifierExpr, Expression> identifier,
        IEnumerable<string>? bound = null, Func<Expression, Expression>? transform = null)
    {
        HashSet<string> parameters = new HashSet<string>(bound ?? [], StringComparer.OrdinalIgnoreCase);
        Expression Visit(Expression node)
        {
            Expression result = node switch
            {
                IdentifierExpr id => parameters.Contains(id.Name) ? id : identifier(id),
                BinaryExpr binary => binary with { Left = Visit(binary.Left), Right = Visit(binary.Right) },
                UnaryExpr unary => unary with { Operand = Visit(unary.Operand) },
                MemberExpr member => member with { Object = Visit(member.Object) },
                IndexExpr index => index with { Object = Visit(index.Object), Index = Visit(index.Index) },
                CallExpr call => call with { Callee = Visit(call.Callee), Args = call.Args.Select(Visit).ToList() },
                FilterExpr filter => filter with { Collection = Visit(filter.Collection), Predicate = Visit(filter.Predicate) },
                LambdaExpr lambda => lambda with
                {
                    Body = Rewrite(lambda.Body, identifier, parameters.Concat(lambda.Params.Select(p => p.Name)), transform)
                },
                ListExpr list => list with { Elements = list.Elements.Select(Visit).ToList() },
                _ => node
            };
            return transform?.Invoke(result) ?? result;
        }
        return Visit(expression);
    }
}
