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
    public override ValueTask<object?> QueryDeferredAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        JavaScriptTypeDeclaration.RegisterCacheFactory();
        JavaScriptMethodDeclaration.RegisterCacheFactory();
        JavaScriptStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new JavaScriptSourceParser());
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(CodeCollectionBuilder.CollectDeferred(
            parsers, query, this, token => JavaScriptProjectDiscovery.DiscoverAsync(
                query.RootPath!, query.ExcludedDirectories, cancellationToken: token)));
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
