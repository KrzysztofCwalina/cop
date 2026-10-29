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

    public override ReadOnlyMemory<byte> GetSchema() => CopSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CopBindings.Build();

    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".cop");
    public override string QueryResultType => "Codebase";

    public override object QueryDeferred(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new CopSourceParser());
        parsers.Register(new TextFileParser());
        var result = CodeCollectionBuilder.CollectDeferred(parsers, new ProviderQuery
        {
            RootPath = query.RootPath, Collection = query.Collection,
            ExcludedDirectories = query.ExcludedDirectories, Options = query.Options
        }, this);
        if (query.RootPath is null) return result;
        var key = Path.GetFullPath(query.RootPath);
        if (!_semantics.TryGetValue(key, out var semantics))
            _semantics.Add(key, semantics = new(key));
        IEnumerable<object> Attach(IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                semantics.Attach(item);
                yield return item;
            }
        }
        var itemTypes = ProviderSchema.FromJson(GetSchema()).Collections.ToDictionary(collection => collection.Name,
            collection => collection.ItemType, StringComparer.Ordinal);
        var accessors = GetRuntimeBindings().Accessors;
        var collections = new Dictionary<string, IEnumerable<object>>(StringComparer.Ordinal);
        foreach (var (name, items) in result.Collections)
        {
            IEnumerable<object> values = Attach(items);
            var filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            if (filter is not null && itemTypes.TryGetValue(name, out var itemType)
                && accessors.TryGetValue(itemType, out var fields))
                values = values.Where(Cop.Lang.FilterCompiler.Compile(filter, fields));
            collections.Add(name, values);
        }
        return new DeferredProviderResult(collections);
    }

    public override object? Query(ProviderQuery query)
    {
        var result = (DeferredProviderResult)QueryDeferred(query);
        return result.Collections.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
    }
}
