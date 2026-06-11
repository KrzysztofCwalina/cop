using Cop.Core;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Rust source code provider. Scans and parses .rs files.
/// </summary>
public class RustProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override object? Query(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new RustSourceParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from Cargo.toml
        if (query.RootPath is not null)
        {
            var projects = RustProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }
}
