// A queryable collection that defers provider execution until materialization.

using Cop.Core;
using Cop.Lang.Ast;
using System.Runtime.CompilerServices;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A queryable collection that defers provider execution until materialization.
/// Accumulates filter expressions from predicate chains and pushes them to the provider.
/// Analogous to LINQ's IQueryable — filters are composed, not executed, until iteration.
/// </summary>
public sealed class CopQueryable : CopValue
{
    /// <summary>
    /// The provider namespace responsible for executing the deferred query.
    /// </summary>
    public string ProviderName { get; }
    /// <summary>
    /// The provider request whose execution is deferred until materialization.
    /// </summary>
    public Cop.Core.ProviderQuery Query { get; }
    private readonly Func<string, Cop.Core.ProviderQuery, CancellationToken,
        ValueTask<CopValue>>? _queryProvider;
    private readonly System.Threading.Lock _materializationGate = new();
    private Lazy<Task<CopValue>>? _materialization;
    private readonly (CopValue Left, CopValue Right)? _concatInputs;
    private Action<string>? _queryLog;
    private CopValue? _materialized;
    /// <summary>
    /// The combined predicate accumulated for provider-side execution.
    /// </summary>
    public Cop.Core.FilterExpression? AccumulatedFilter { get; }
    /// <summary>
    /// The optional Cop type name of the collection's elements.
    /// </summary>
    public string? ItemType { get; }
    private readonly IReadOnlyDictionary<string, string>? _scalarProperties;
    private QueryReuseScope _reuse;
    private QueryReuseStage? _stage;
    internal QuerySource? Source { get; private set; }

    /// <summary>Source-shaped Cop for this provider query, without enumerating it.</summary>
    public string CopQuery => Query.Collection is not null && Source is { } source ? source.Program
        : throw new NotSupportedException("Cop source requires one named provider collection. Use -d for concatenation's individual provider queries; some programmatic filters also have no portable source.");

    /// <summary>
    /// Defers execution of a provider collection with optional filter and element metadata.
    /// </summary>
    public CopQueryable(string providerName, Cop.Core.ProviderQuery query,
        Func<string, Cop.Core.ProviderQuery, CancellationToken, ValueTask<CopValue>> queryProvider,
        CopQueryableOptions? options = null)
    {
        ProviderName = providerName;
        Query = query;
        _queryProvider = queryProvider;
        AccumulatedFilter = options?.Filter ?? query.Filter;
        ItemType = options?.ItemType;
        _scalarProperties = options?.ScalarProperties;
        _reuse = new();
        Source = query.Collection is { } collection
            ? new QuerySource(collection, $"{providerName}.{collection} root={query.RootPath}") : null;
        if (AccumulatedFilter is { } initial)
        {
            Source = QuerySource.FromFilter(initial) is { } step ? Source?.Append(step) : null;
        }

        RegisterStage();
    }

    private CopQueryable(CopQueryable original, Cop.Core.FilterExpression filter, QuerySource? source)
    {
        ProviderName = original.ProviderName;
        Query = original.Query;
        _queryProvider = original._queryProvider;
        _concatInputs = original._concatInputs;
        _queryLog = original._queryLog;
        AccumulatedFilter = original.AccumulatedFilter is null ? filter
            : Cop.Core.FilterExpression.And(original.AccumulatedFilter, filter);
        ItemType = original.ItemType;
        _scalarProperties = original._scalarProperties;
        _reuse = original._reuse;
        Source = source;
        if (original._concatInputs is { } inputs)
        {
            _concatInputs = (Distribute(inputs.Left), Distribute(inputs.Right));
        }

        RegisterStage();

        CopValue Distribute(CopValue input)
        {
            if (input is CopQueryable query)
            {
                return new CopQueryable(query, filter, source is null ? null
                    : query.Source?.Append(source.Steps.Skip(original.Source?.Steps.Count ?? 0)));
            }

            return new CopLazyCollection(token =>
            {
                if (QuerySource.FromFilter(filter) is { } step)
                {
                    QuerySource.Of(input)?.Local(expression => step.Apply(expression, step.Predicate),
                        "filter after a local concatenation input");
                }

                return FilterAsync(input, filter, token);
            });
        }
    }

    private CopQueryable(CopValue left, CopValue right)
    {
        ProviderName = "concat";
        Query = new();
        _concatInputs = (left, right);
        ItemType = (left as CopQueryable)?.ItemType ?? (right as CopQueryable)?.ItemType;
        _scalarProperties = (left as CopQueryable)?._scalarProperties ?? (right as CopQueryable)?._scalarProperties;
        _reuse = new();
        Source = QuerySource.Concat(left, right);
    }

    internal void SetQueryLog(Action<string>? log)
    {
        _queryLog = log;
        Source = Source?.WithLog(log);
        if (_stage is not null)
        {
            _stage.Source = Source;
        }
    }

    internal void SetReuseScope(QueryReuseScope scope)
    {
        _reuse = scope;
        RegisterStage();
    }

    private void RegisterStage()
    {
        if (_concatInputs is null && Query.Collection is not null && _scalarProperties is not null
            && Query.CollectionFilters is not { Count: > 0 } && AccumulatedFilter is { } filter
            && CanPushFilter(filter) && QueryReuseScope.IsReusable(filter))
        {
            _stage = _reuse.Register(ProviderName, Query, filter, Source);
        }
    }

    /// <summary>
    /// Tests whether all filter operations target supported scalar properties.
    /// </summary>
    public bool CanPushFilter(Cop.Core.FilterExpression filter) => _scalarProperties is null || filter switch
    {
        Cop.Core.PropertyFilter f => _scalarProperties.GetValueOrDefault(f.Property) == "bool",
        Cop.Core.StringOpFilter f => _scalarProperties.GetValueOrDefault(f.Property) == "string",
        Cop.Core.ComparisonFilter f => _scalarProperties.GetValueOrDefault(f.Property) is "int" or "float",
        Cop.Core.AndFilter f => f.Conditions.All(CanPushFilter),
        Cop.Core.OrFilter f => f.Conditions.All(CanPushFilter),
        Cop.Core.NotFilter f => CanPushFilter(f.Inner),
        _ => false
    };
    internal bool HasScalarProperty(string name) => _scalarProperties?.ContainsKey(name) == true;
    /// <summary>
    /// Returns a new queryable with an additional filter accumulated (immutable).
    /// Multiple filters are combined with AND.
    /// </summary>
    public CopQueryable WithFilter(Cop.Core.FilterExpression filter)
    {
        QuerySource? source = QuerySource.FromFilter(filter) is { } step ? Source?.Append(step) : null;
        return new CopQueryable(this, filter, source);
    }

    internal CopQueryable WithFilter(Cop.Core.FilterExpression filter, QueryStep step) =>
        new(this, filter, Source?.Append(step));

    /// <summary>
    /// Materializes the queryable by calling the provider with accumulated filters.
    /// Returns the provider result as a CopValue (typically CopList).
    /// </summary>
    public async ValueTask<CopValue> MaterializeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lazy<Task<CopValue>> pending;
        lock (_materializationGate)
        {
            if (_materialized is not null)
            {
                return _materialized;
            }
            pending = _materialization ??= new(() => MaterializeCoreAsync(cancellationToken));
        }
        try
        {
            return await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pending.IsValueCreated && pending.Value.IsCompleted)
            {
                lock (_materializationGate)
                {
                    if (ReferenceEquals(_materialization, pending))
                    {
                        _materialization = null;
                    }
                }
            }
        }
    }

    private async Task<CopValue> MaterializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_materialized is not null)
        {
            return _materialized;
        }

        if (_concatInputs is { } inputs)
        {
            Source?.Local(expression => expression, "concatenation executes locally; filters are distributed to provider inputs");
            _materialized = new CopLazyCollection(EnumerateInputs);
            return _materialized;

            async IAsyncEnumerable<CopValue> EnumerateInputs(
                [EnumeratorCancellation] CancellationToken token)
            {
                await foreach (CopValue item in CollectionValues.EnumerateAsync(inputs.Left, token)
                    .ConfigureAwait(false))
                {
                    yield return item;
                }

                await foreach (CopValue item in CollectionValues.EnumerateAsync(inputs.Right, token)
                    .ConfigureAwait(false))
                {
                    yield return item;
                }
            }
        }
        if (Source is null)
        {
            _queryLog?.Invoke($"[query-cop-unavailable] {ProviderName}.{Query.Collection}: unnamed collection or filter without portable source");
        }

        if (_stage is not null)
        {
            return _materialized = await _stage.MaterializeAsync(ExecuteProviderAsync, Source,
                cancellationToken).ConfigureAwait(false);
        }

        Source?.ReportProvider();
        return _materialized = await ExecuteProviderAsync(AccumulatedFilter, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<CopValue> ExecuteProviderAsync(Cop.Core.FilterExpression? filter,
        CancellationToken cancellationToken)
    {
        ProviderQuery queryWithFilter = new Cop.Core.ProviderQuery
        {
            RootPath = Query.RootPath,
            Collection = Query.Collection,
            Filter = filter,
            CollectionFilters = Query.CollectionFilters,
            ExcludedDirectories = Query.ExcludedDirectories,
            Options = Query.Options
        };
        CopValue result = await _queryProvider!(ProviderName, queryWithFilter, cancellationToken)
            .ConfigureAwait(false);
        if (filter is null || result is CopLazyCollection lazy && QueryReuseScope.SameFilter(lazy.AppliedFilter, filter))
        {
            return result;
        }

        return new CopLazyCollection(token => FilterAsync(result, filter, token))
            { AppliedFilter = filter };
    }

    /// <summary>
    /// Materializes and returns items as an enumerable of CopValues.
    /// </summary>
    public async IAsyncEnumerable<CopValue> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CopValue result = await MaterializeAsync(cancellationToken).ConfigureAwait(false);
        await foreach (CopValue item in CollectionValues.EnumerateAsync(result, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    internal static async IAsyncEnumerable<CopValue> FilterAsync(CopValue source,
        FilterExpression filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (CopValue item in CollectionValues.EnumerateAsync(source, cancellationToken)
            .ConfigureAwait(false))
        {
            if (await CopFilter.MatchesAsync(filter, item, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    /// <inheritdoc/>
    public override string Display() => $"<queryable {ProviderName}>";
    /// <inheritdoc/>
    public override string ToString() => Display();

    /// <summary>
    /// Combines collections while retaining deferred execution and source provenance.
    /// </summary>
    public static CopQueryable Concat(CopValue left, CopValue right)
        => new(left, right);
}
