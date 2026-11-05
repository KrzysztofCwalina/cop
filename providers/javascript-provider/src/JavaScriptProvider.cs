// Registers JavaScript parsing, project discovery, schemas, and runtime bindings.

using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// JavaScript/TypeScript source code provider. Scans and parses .js and .ts files.
/// </summary>
public class JavaScriptProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => JavaScriptSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => JavaScriptBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".mts", ".cts");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    /// <inheritdoc />
    public override object QueryDeferred(ProviderQuery query)
    {
        JavaScriptTypeDeclaration.RegisterCacheFactory();
        JavaScriptMethodDeclaration.RegisterCacheFactory();
        JavaScriptStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new JavaScriptSourceParser());
        return CodeCollectionBuilder.CollectDeferred(parsers, query, this,
            () => JavaScriptProjectDiscovery.Discover(query.RootPath!, query.ExcludedDirectories));
    }

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        JavaScriptTypeDeclaration.RegisterCacheFactory();
        JavaScriptMethodDeclaration.RegisterCacheFactory();
        JavaScriptStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new JavaScriptSourceParser());
        Dictionary<string, List<object>> collections = CodeCollectionBuilder.CollectAndParse(parsers, query, this);

        // Discover projects from package.json
        if (query.RootPath is not null)
        {
            List<ProjectInfo> projects = JavaScriptProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
            {
                collections["Projects"] = projects.Cast<object>().ToList();
            }
        }

        return ApplyQueryFilters(query, collections);
    }
}
