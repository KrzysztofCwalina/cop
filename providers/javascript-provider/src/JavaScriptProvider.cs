using Cop.Core;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// JavaScript/TypeScript source code provider. Scans and parses .js and .ts files.
/// </summary>
public class JavaScriptProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new JavaScriptSourceParser());
        parsers.Register(new TextFileParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from package.json
        if (query.RootPath is not null)
        {
            var projects = JavaScriptProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.RequestedCollections is null || query.RequestedCollections.Contains("Projects"))
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }
}
