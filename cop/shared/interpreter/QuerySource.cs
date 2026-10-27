namespace Cop.Lang.Interpreter;

using Cop.Core;
using Cop.Lang.Ast;
using Expression = Cop.Lang.Ast.Expression;
using IdentifierExpr = Cop.Lang.Ast.IdentifierExpr;
using LiteralExpr = Cop.Lang.Ast.LiteralExpr;
using BinaryExpr = Cop.Lang.Ast.BinaryExpr;
using UnaryExpr = Cop.Lang.Ast.UnaryExpr;
using CallExpr = Cop.Lang.Ast.CallExpr;

internal sealed record QueryStep(Expression Predicate, IReadOnlyList<FunctionDecl> Definitions,
    bool Negated = false, string? Property = null, string? Method = null)
{
    public Expression Apply(Expression source, Expression predicate) => Method is not null
        ? new CallExpr(new MemberExpr(source, Method), [predicate])
        : new FilterExpr(Property is null ? source : new FilterExpr(source, new IdentifierExpr(Property)),
            predicate, Negated);
}

/// <summary>Source provenance is deliberately separate from ProviderQuery and its wire/cache identity.</summary>
internal sealed class QuerySource
{
    private readonly Expression _root;
    private readonly IReadOnlyList<FunctionDecl> _rootDefinitions;
    private readonly Action<string>? _log;
    private readonly string _context;
    private readonly IReadOnlySet<string> _roots;
    public IReadOnlyList<QueryStep> Steps { get; }

    public QuerySource(string collection, string context, Action<string>? log = null)
        : this(new IdentifierExpr(collection), [], [], context, log, new HashSet<string> { collection }) { }

    private QuerySource(Expression root, IReadOnlyList<FunctionDecl> definitions, IReadOnlyList<QueryStep> steps,
        string context, Action<string>? log, IReadOnlySet<string> roots)
    {
        _root = root;
        _rootDefinitions = definitions;
        Steps = steps;
        _context = context;
        _log = log;
        _roots = roots;
    }

    public QuerySource Append(QueryStep step) => Append([step]);
    public QuerySource Append(IEnumerable<QueryStep> steps) =>
        new(_root, _rootDefinitions, Steps.Concat(steps).ToArray(), _context, _log, _roots);
    public QuerySource WithLog(Action<string>? log) => new(_root, _rootDefinitions, Steps, _context, log, _roots);
    public QuerySource? ForFilter(FilterExpression filter) => FromFilter(filter) is { } step
        ? new(_root, _rootDefinitions, [step], _context, _log, _roots) : null;

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
            foreach (var condition in conditions)
            {
                if (Convert(condition) is not { } expression) return null;
                result = result is null ? expression : new BinaryExpr(result, op, expression);
            }
            return result ?? new LiteralExpr(op == BinaryOp.And);
        }
        static Expression Field(string name) => new MemberExpr(new IdentifierExpr("item"), name);
        return Convert(filter) is { } body
            ? new QueryStep(new LambdaExpr([new Parameter("item")], body), [], Method: "Where") : null;
    }

    public string Program
    {
        get
        {
            var (expression, definitions) = Build();
            return string.Join("\n", definitions.Select(QuerySourceWriter.Predicate)
                .Append("command MAIN = " + QuerySourceWriter.Render(expression)));
        }
    }

    public void ReportProvider() => _log?.Invoke($"[query-cop] {_context}\n{Program}");

    public QuerySource Local(Func<Expression, Expression> operation, string reason)
    {
        var (expression, definitions) = Build();
        var local = operation(expression);
        // Local expressions may be arbitrary Cop, not the portable/pushable source subset.
        _log?.Invoke($"[query-local] {_context}: {reason}\n{(QuerySourceWriter.CanRender(local)
            ? QuerySourceWriter.Render(local) : $"Local {local.GetType().Name} at line {local.Line}; not a portable provider query")}");
        return new(local, definitions, [], _context, _log, _roots);
    }

    public static QuerySource? Of(CopValue value) => value switch
    {
        CopQueryable query => query.Source,
        CopLazyCollection lazy => lazy.Source,
        CopList list => list.Source,
        CopThunk { ForcedValue: { } forced } => Of(forced),
        _ => null
    };

    public static CopValue TrackLocal(CopValue input, CopValue result, Func<Expression, Expression> operation, string reason)
    {
        if (Of(input) is not { } source) return result;
        var local = source.Local(operation, reason);
        switch (result)
        {
            case CopLazyCollection lazy: lazy.Source = local; break;
            case CopList list: list.Source = local; break;
        }
        return result;
    }

    public static QuerySource? Concat(CopValue left, CopValue right)
    {
        var leftSource = Of(left);
        var rightSource = Of(right);
        var context = leftSource ?? rightSource;
        if (context is null) return null;
        var roots = new HashSet<string>((leftSource?._roots.AsEnumerable() ?? [])
            .Concat(rightSource?._roots.AsEnumerable() ?? []));
        var builder = new Builder(roots);
        var a = leftSource?.Build() ?? (new IdentifierExpr("local"), (IReadOnlyList<FunctionDecl>)[]);
        var b = rightSource?.Build() ?? (new IdentifierExpr("local"), (IReadOnlyList<FunctionDecl>)[]);
        var expression = new BinaryExpr(builder.Import(a.Item1, a.Item2), BinaryOp.Add,
            builder.Import(b.Item1, b.Item2));
        return new(expression, builder.Definitions.ToArray(), [], context._context, context._log, roots);
    }

    private (Expression, IReadOnlyList<FunctionDecl>) Build()
    {
        var builder = new Builder(_roots);
        Expression expression = builder.Import(_root, _rootDefinitions);
        foreach (var step in Steps)
            expression = step.Apply(expression, builder.Import(step.Predicate, step.Definitions));
        return (expression, builder.Definitions);
    }

    private sealed class Builder(IReadOnlySet<string> roots)
    {
        public List<FunctionDecl> Definitions { get; } = [];

        public Expression Import(Expression expression, IReadOnlyList<FunctionDecl> definitions)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                var body = Rewrite(((ExpressionBody)definition.Body).Expr, id =>
                    names.TryGetValue(id.Name, out var name) ? id with { Name = name } : id,
                    definition.Params.Select(p => p.Name));
                var candidate = definition with { Body = new ExpressionBody(body) };
                string name = definition.Name;
                int suffix = 2;
                while (roots.Contains(name) || Definitions.FirstOrDefault(d => d.Name == name) is { } existing
                    && QuerySourceWriter.Predicate(existing) != QuerySourceWriter.Predicate(candidate with { Name = name }))
                    name = definition.Name + "__" + suffix++;
                names[definition.Name] = name;
                if (Definitions.All(d => d.Name != name))
                    Definitions.Add(candidate with { Name = name });
            }
            return Rewrite(expression, id => names.TryGetValue(id.Name, out var name) ? id with { Name = name } : id);
        }
    }

    internal static Expression Rewrite(Expression expression, Func<IdentifierExpr, Expression> identifier,
        IEnumerable<string>? bound = null, Func<Expression, Expression>? transform = null)
    {
        var parameters = new HashSet<string>(bound ?? [], StringComparer.OrdinalIgnoreCase);
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

internal sealed record ResolvedQueryPredicate(Expression Expression, Environment Environment,
    IReadOnlyList<FunctionDecl> Definitions)
{
    public static ResolvedQueryPredicate Resolve(Expression expression, Environment environment, string? itemType,
        bool element = false)
    {
        var definitions = new List<FunctionDecl>();
        var names = new Dictionary<CopFunction, string>();
        var scope = environment.Extend();

        Expression ResolveExpression(Expression node, Environment closure, IEnumerable<string>? bound = null) =>
            QuerySource.Rewrite(node, id =>
            {
                if (TryLiteral(id, closure, new HashSet<CopThunk>()) is { } literal) return literal;
                if (PredicateCompiler.FindPredicate(id.Name, closure, itemType) is not { } function) return id;
                if (names.TryGetValue(function, out var existingName)) return id with { Name = existingName };
                string name = id.Name;
                int suffix = 2;
                while (names.Values.Contains(name, StringComparer.Ordinal))
                    name = id.Name + "__" + suffix++;
                names.Add(function, name);
                var declaration = function.Declaration;
                var body = ResolveExpression(((ExpressionBody)declaration.Body).Expr, function.Closure,
                    declaration.Params.Select(p => p.Name));
                var resolved = declaration with { Name = name, Body = new ExpressionBody(body) };
                definitions.Add(resolved);
                scope.Define(name, new CopFunction(resolved, scope));
                return id with { Name = name };
            }, bound, node => node switch
            {
                UnaryExpr { Op: UnaryOp.Negate, Operand: LiteralExpr { Value: int integer } } => new LiteralExpr(-integer),
                UnaryExpr { Op: UnaryOp.Negate, Operand: LiteralExpr { Value: double number } } => new LiteralExpr(-number),
                _ => node
            });

        var resolvedExpression = ResolveExpression(expression, environment, element ? ["item"] : []);
        return new(resolvedExpression, scope, definitions);
    }

    private static LiteralExpr? TryLiteral(IdentifierExpr identifier, Environment environment, HashSet<CopThunk> expanding)
    {
        if (!environment.TryLookup(identifier.Name, out var value)) return null;
        if (value is CopThunk thunk)
        {
            if (!expanding.Add(thunk)) return null;
            if (thunk.ForcedValue is { } forced) value = forced;
            else if (thunk.ConstantExpression is LiteralExpr literal) return literal;
            else if (thunk.ConstantExpression is IdentifierExpr alias && thunk.ConstantEnvironment is { } closure)
                return TryLiteral(alias, closure, expanding);
            else return null;
        }
        return value switch
        {
            CopString text => new LiteralExpr(text.Value),
            CopInt integer => new LiteralExpr(integer.Value),
            CopNumber number when double.IsFinite(number.Value) => new LiteralExpr(number.Value),
            CopBool boolean => new LiteralExpr(boolean.Value),
            CopNull => new LiteralExpr(null),
            _ => null
        };
    }
}
