// Supplies Cop source parsing and language-specific runtime bindings.

using Cop.Core;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Cop language source code provider. Scans and parses .cop files,
/// exposing their structure (types, predicates, functions, imports) through
/// the shared code analysis collections, with CopFunction declarations in Methods.
/// </summary>
public class CopProvider : DataProvider
{
    private readonly Dictionary<string, CopSemanticSnapshot> _semantics = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => CopSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => CopBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".cop");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    /// <inheritdoc />
    public override ValueTask<object?> QueryDeferredAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new CopSourceParser());
        parsers.Register(new TextFileParser());
        DeferredProviderResult result = CodeCollectionBuilder.CollectDeferred(parsers, new ProviderQuery
        {
            RootPath = query.RootPath, Collection = query.Collection,
            ExcludedDirectories = query.ExcludedDirectories, Options = query.Options
        }, this);
        if (query.RootPath is null)
        {
            return ValueTask.FromResult<object?>(result);
        }

        string key = Path.GetFullPath(query.RootPath);
        if (!_semantics.TryGetValue(key, out CopSemanticSnapshot? semantics))
        {
            _semantics.Add(key, semantics = new(key));
        }

        async IAsyncEnumerable<object> Attach(
            IAsyncEnumerable<object> items,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token = default)
        {
            await foreach (object item in items.WithCancellation(token).ConfigureAwait(false))
            {
                semantics.Attach(item);
                yield return item;
            }
        }
        Dictionary<string, string> itemTypes = ProviderSchema.FromJson(GetSchema()).Collections.ToDictionary(collection => collection.Name,
            collection => collection.ItemType, StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, Func<object, object?>>> accessors = GetRuntimeBindings().Accessors;
        Dictionary<string, IAsyncEnumerable<object>> collections = new(StringComparer.Ordinal);
        AsyncProviderFilter evaluator = new(GetRuntimeBindings());
        async IAsyncEnumerable<object> Filter(
            IAsyncEnumerable<object> values, FilterExpression? filter,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token = default)
        {
            await foreach (object value in values.WithCancellation(token).ConfigureAwait(false))
            {
                if (await evaluator.MatchesAsync(value, filter, token).ConfigureAwait(false))
                {
                    yield return value;
                }
            }
        }
        foreach ((string name, IAsyncEnumerable<object> items) in result.Collections)
        {
            IAsyncEnumerable<object> values = Attach(items);
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            if (filter is not null && itemTypes.TryGetValue(name, out string? itemType)
                && accessors.TryGetValue(itemType, out Dictionary<string, Func<object, object?>>? fields))
            {
                values = Filter(values, filter);
            }

            collections.Add(name, values);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(new DeferredProviderResult(collections));
    }

    /// <inheritdoc />
    public override async ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        DeferredProviderResult result = (DeferredProviderResult)
            (await QueryDeferredAsync(query, cancellationToken).ConfigureAwait(false))!;
        return await ProviderCollections.MaterializeAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }
}
