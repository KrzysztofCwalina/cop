using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Rust source code provider. Scans and parses .rs files.
/// </summary>
public class RustProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => RustSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => RustBindings.Build();

    public override object? Query(ProviderQuery query)
    {
        // Ensure cached Rust types reconstruct as RustTypeDeclaration (not the plain base)
        // before the source cache is read in CollectAndParse.
        RustTypeDeclaration.RegisterCacheFactory();
        RustMethodDeclaration.RegisterCacheFactory();
        RustStatementInfo.RegisterCacheFactory();

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
