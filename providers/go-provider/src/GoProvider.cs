using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Go source code provider. Scans and parses .go files.
/// </summary>
public class GoProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => GoSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => GoBindings.Build();

    public override object? Query(ProviderQuery query)
    {
        GoTypeDeclaration.RegisterCacheFactory();
        var parsers = new SourceParserRegistry();
        parsers.Register(new GoSourceParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from go.mod
        if (query.RootPath is not null)
        {
            var projects = GoProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }
}
