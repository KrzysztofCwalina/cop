// Reuses materialized query prefixes and applies remaining filters lazily.

using System.Text.Json;
using Cop.Core;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Reuses materialized query prefixes and applies remaining filters lazily.
/// </summary>
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
            QueryReuseStage? shared = parent;
            while (shared is not null && shared._consumers < 2 && shared._result is null && shared._failure is null)
            {
                shared = shared.Parent;
            }

            if (shared is null)
            {
                (requestedSource ?? Source)?.ReportProvider();
                return _result = execute(filter);
            }
            CopValue input = shared.Materialize(execute, shared.Source);
            FilterExpression remaining = QueryReuseScope.Combine(conditions.Skip(shared.Depth));
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
