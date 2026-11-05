// Registers Go parsing, project discovery, schemas, and runtime bindings.

using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Go source code provider. Scans and parses .go files.
/// </summary>
public class GoProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => GoSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => GoBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".go");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    /// <inheritdoc />
    public override object QueryDeferred(ProviderQuery query)
    {
        GoTypeDeclaration.RegisterCacheFactory();
        GoMethodDeclaration.RegisterCacheFactory();
        GoStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new GoSourceParser());
        return CodeCollectionBuilder.CollectDeferred(parsers, query, this,
            () => GoProjectDiscovery.Discover(query.RootPath!, query.ExcludedDirectories));
    }

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        GoTypeDeclaration.RegisterCacheFactory();
        GoMethodDeclaration.RegisterCacheFactory();
        GoStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new GoSourceParser());
        Dictionary<string, List<object>> collections = CodeCollectionBuilder.CollectAndParse(parsers, query, this);

        // Discover projects from go.mod
        if (query.RootPath is not null)
        {
            List<ProjectInfo> projects = GoProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
            {
                collections["Projects"] = projects.Cast<object>().ToList();
            }
        }

        return ApplyQueryFilters(query, collections);
    }
}
