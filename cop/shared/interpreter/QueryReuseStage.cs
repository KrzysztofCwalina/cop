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
    private readonly System.Threading.Lock _gate = new();
    private Lazy<Task<CopValue>>? _materialization;
    /// <summary>
    /// The optional portable source representation associated with this stage.
    /// </summary>
    public QuerySource? Source { get; set; }

    /// <summary>
    /// Marks the stage as shared when more than one consumer references it.
    /// </summary>
    public void AddConsumer() => _consumers = Math.Min(2, _consumers + 1);

    /// <summary>
    /// Executes the stage once and reuses its result or captured failure.
    /// </summary>
    public ValueTask<CopValue> MaterializeAsync(
        Func<FilterExpression, CancellationToken, ValueTask<CopValue>> execute,
        QuerySource? requestedSource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lazy<Task<CopValue>> pending;
        bool isReused;
        lock (_gate)
        {
            isReused = _materialization is not null;
            pending = _materialization ??= new(() =>
                MaterializeCoreAsync(execute, requestedSource, cancellationToken));
        }
        if (isReused)
        {
            requestedSource?.Local(expression => expression, "shared query result reused");
        }
        return new(pending.Value.WaitAsync(cancellationToken));
    }

    private async Task<CopValue> MaterializeCoreAsync(
        Func<FilterExpression, CancellationToken, ValueTask<CopValue>> execute,
        QuerySource? requestedSource, CancellationToken cancellationToken)
    {
        _failure?.Throw();
        try
        {
            QueryReuseStage? shared = parent;
            while (shared is not null && shared._consumers < 2 && shared._result is null && shared._failure is null)
            {
                shared = shared.Parent;
            }

            if (shared is null)
            {
                (requestedSource ?? Source)?.ReportProvider();
                return _result = await execute(filter, cancellationToken).ConfigureAwait(false);
            }
            CopValue input = await shared.MaterializeAsync(execute, shared.Source,
                cancellationToken).ConfigureAwait(false);
            FilterExpression remaining = QueryReuseScope.Combine(conditions.Skip(shared.Depth));
            requestedSource?.Local(expression => expression,
                $"shared prefix ({FilterExpression.Format(shared.Filter)}); remaining filter executes locally");
            return _result = new CopLazyCollection(token => CopQueryable.FilterAsync(input,
                remaining, token)) { AppliedFilter = filter };
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
