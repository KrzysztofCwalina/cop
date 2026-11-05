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
    public override object QueryDeferred(ProviderQuery query)
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
            return result;
        }

        string key = Path.GetFullPath(query.RootPath);
        if (!_semantics.TryGetValue(key, out CopSemanticSnapshot? semantics))
        {
            _semantics.Add(key, semantics = new(key));
        }

        IEnumerable<object> Attach(IEnumerable<object> items)
        {
            foreach (object item in items)
            {
                semantics.Attach(item);
                yield return item;
            }
        }
        Dictionary<string, string> itemTypes = ProviderSchema.FromJson(GetSchema()).Collections.ToDictionary(collection => collection.Name,
            collection => collection.ItemType, StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, Func<object, object?>>> accessors = GetRuntimeBindings().Accessors;
        Dictionary<string, IEnumerable<object>> collections = new Dictionary<string, IEnumerable<object>>(StringComparer.Ordinal);
        foreach ((string? name, IEnumerable<object>? items) in result.Collections)
        {
            IEnumerable<object> values = Attach(items);
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            if (filter is not null && itemTypes.TryGetValue(name, out string? itemType)
                && accessors.TryGetValue(itemType, out Dictionary<string, Func<object, object?>>? fields))
            {
                values = values.Where(Cop.Lang.FilterCompiler.Compile(filter, fields));
            }

            collections.Add(name, values);
        }
        return new DeferredProviderResult(collections);
    }

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        DeferredProviderResult result = (DeferredProviderResult)QueryDeferred(query);
        return result.Collections.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
    }
}
