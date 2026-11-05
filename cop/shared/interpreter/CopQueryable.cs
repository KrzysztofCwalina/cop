// A queryable collection that defers provider execution until materialization.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A queryable collection that defers provider execution until materialization.
/// Accumulates filter expressions from predicate chains and pushes them to the provider.
/// Analogous to LINQ's IQueryable — filters are composed, not executed, until iteration.
/// </summary>
public sealed class CopQueryable : CopValue
{
    public string ProviderName { get; }
    public Cop.Core.ProviderQuery Query { get; }
    private readonly Func<string, Cop.Core.ProviderQuery, CopValue>? _queryProvider;
    private readonly (CopValue Left, CopValue Right)? _concatInputs;
    private Action<string>? _queryLog;
    private CopValue? _materialized;
    public Cop.Core.FilterExpression? AccumulatedFilter { get; }
    public string? ItemType { get; }
    private readonly IReadOnlyDictionary<string, string>? _scalarProperties;
    private QueryReuseScope _reuse;
    private QueryReuseStage? _stage;
    internal QuerySource? Source { get; private set; }

    /// <summary>Source-shaped Cop for this provider query, without enumerating it.</summary>
    public string CopQuery => Query.Collection is not null && Source is { } source ? source.Program
        : throw new NotSupportedException("Cop source requires one named provider collection. Use -d for concatenation's individual provider queries; some programmatic filters also have no portable source.");

    public CopQueryable(string providerName, Cop.Core.ProviderQuery query, Func<string, Cop.Core.ProviderQuery, CopValue> queryProvider, Cop.Core.FilterExpression? filter = null, string? itemType = null, IReadOnlyDictionary<string, string>? scalarProperties = null)
    {
        ProviderName = providerName;
        Query = query;
        _queryProvider = queryProvider;
        AccumulatedFilter = filter ?? query.Filter;
        ItemType = itemType;
        _scalarProperties = scalarProperties;
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

            return new CopLazyCollection(() =>
            {
                if (QuerySource.FromFilter(filter) is { } step)
                {
                    QuerySource.Of(input)?.Local(expression => step.Apply(expression, step.Predicate),
                        "filter after a local concatenation input");
                }

                return CollectionValues.Enumerate(input).Where(item => CopFilter.Matches(filter, item));
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
    public CopValue Materialize()
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

            IEnumerable<CopValue> EnumerateInputs()
            {
                foreach (CopValue item in CollectionValues.Enumerate(inputs.Left))
                {
                    yield return item;
                }

                foreach (CopValue item in CollectionValues.Enumerate(inputs.Right))
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
            return _materialized = _stage.Materialize(ExecuteProvider, Source);
        }

        Source?.ReportProvider();
        return _materialized = ExecuteProvider(AccumulatedFilter);
    }

    private CopValue ExecuteProvider(Cop.Core.FilterExpression? filter)
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
        CopValue result = _queryProvider!(ProviderName, queryWithFilter);
        if (filter is null || result is CopLazyCollection lazy && QueryReuseScope.SameFilter(lazy.AppliedFilter, filter))
        {
            return result;
        }

        return new CopLazyCollection(() => CollectionValues.Enumerate(result)
            .Where(item => CopFilter.Matches(filter, item))) { AppliedFilter = filter };
    }

    /// <summary>
    /// Materializes and returns items as an enumerable of CopValues.
    /// </summary>
    public IEnumerable<CopValue> Enumerate()
    {
        CopValue result = Materialize();
        return result switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => lazy.Enumerate(),
            _ => [result]
        };
    }

    public override string Display() => $"<queryable {ProviderName}>";
    public override string ToString() => Display();

    public static CopQueryable Concat(CopValue left, CopValue right)
        => new(left, right);
}
