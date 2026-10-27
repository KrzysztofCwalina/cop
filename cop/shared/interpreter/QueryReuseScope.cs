using System.Text.Json;
using Cop.Core;

namespace Cop.Lang.Interpreter;

/// <summary>Execution-scoped lookup for identical, ordered scalar-filter prefixes.</summary>
internal sealed class QueryReuseScope
{
    internal const int Capacity = 256;
    private readonly Dictionary<string, (WeakReference<QueryReuseStage> Stage, LinkedListNode<string> Node)> _stages = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    internal int Count => _stages.Count;

    public QueryReuseStage Register(string provider, ProviderQuery query, FilterExpression filter, QuerySource? source)
    {
        var conditions = Flatten(filter).ToArray();
        QueryReuseStage? parent = null;
        for (int count = 1; count <= conditions.Length; count++)
        {
            var prefix = count == conditions.Length ? filter : Combine(conditions.Take(count));
            var key = provider.ToUpperInvariant() + ":" + JsonSerializer.Serialize(WithFilter(query, prefix));
            if (_stages.TryGetValue(key, out var entry) && entry.Stage.TryGetTarget(out var existing))
            {
                _recent.Remove(entry.Node);
                _recent.AddFirst(entry.Node);
                parent = existing;
                continue;
            }
            if (entry.Node is not null) _recent.Remove(entry.Node);
            var stage = new QueryReuseStage(prefix, conditions.Take(count).ToArray(), parent)
            {
                Source = source?.ForFilter(prefix)
            };
            parent?.AddConsumer();
            var node = _recent.AddFirst(key);
            _stages[key] = (new(stage), node);
            if (_stages.Count > Capacity)
            {
                _stages.Remove(_recent.Last!.Value);
                _recent.RemoveLast();
            }
            parent = stage;
        }
        parent!.Source = source;
        return parent;
    }

    public static bool IsReusable(FilterExpression filter) => filter switch
    {
        PropertyFilter => true,
        StringOpFilter text => text.Op is StringOp.StartsWith or StringOp.EndsWith or StringOp.Contains
            or StringOp.Equals or StringOp.ExactEquals or StringOp.Same,
        ComparisonFilter number => double.IsFinite(number.Value) && Enum.IsDefined(number.Op),
        AndFilter and => and.Conditions.Count > 0 && and.Conditions.All(IsReusable),
        OrFilter or => or.Conditions.All(IsReusable),
        NotFilter not => IsReusable(not.Inner),
        _ => false
    };

    public static bool SameFilter(FilterExpression? left, FilterExpression? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && JsonSerializer.Serialize<FilterExpression>(left) == JsonSerializer.Serialize<FilterExpression>(right);

    public static ProviderQuery WithFilter(ProviderQuery query, FilterExpression filter) => new()
    {
        RootPath = query.RootPath, Collection = query.Collection, Filter = filter,
        CollectionFilters = query.CollectionFilters, ExcludedDirectories = query.ExcludedDirectories, Options = query.Options
    };

    internal static FilterExpression Combine(IEnumerable<FilterExpression> conditions)
    {
        var list = conditions.ToList();
        return list.Count == 1 ? list[0] : new AndFilter(list);
    }

    private static IEnumerable<FilterExpression> Flatten(FilterExpression filter) =>
        filter is AndFilter and ? and.Conditions.SelectMany(Flatten) : [filter];
}

internal sealed class QueryReuseStage(FilterExpression filter, FilterExpression[] conditions, QueryReuseStage? parent)
{
    private CopValue? _result;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;
    private int _consumers;
    public QuerySource? Source { get; set; }

    public void AddConsumer() => _consumers = Math.Min(2, _consumers + 1);

    public CopValue Materialize(Func<FilterExpression, CopValue> execute, QuerySource? requestedSource)
    {
        _failure?.Throw();
        try
        {
            if (_result is not null)
            {
                requestedSource?.Local(expression => expression, "shared query result reused");
                return _result;
            }
            var shared = parent;
            while (shared is not null && shared._consumers < 2 && shared._result is null && shared._failure is null)
                shared = shared.Parent;
            if (shared is null)
            {
                (requestedSource ?? Source)?.ReportProvider();
                return _result = execute(filter);
            }
            var input = shared.Materialize(execute, shared.Source);
            var remaining = QueryReuseScope.Combine(conditions.Skip(shared.Depth));
            requestedSource?.Local(expression => expression,
                $"shared prefix ({FilterExpression.Format(shared.Filter)}); remaining filter executes locally");
            return _result = new CopLazyCollection(() => CollectionValues.Enumerate(input)
                .Where(item => CopFilter.Matches(remaining, item))) { AppliedFilter = filter };
        }
        catch (Exception exception)
        {
            _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
            throw;
        }
    }

    private QueryReuseStage? Parent => parent;
    private int Depth => conditions.Length;
    private FilterExpression Filter => filter;
}
