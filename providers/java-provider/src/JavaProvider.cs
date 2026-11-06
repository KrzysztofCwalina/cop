// Registers Java parsing, project discovery, schemas, and runtime bindings.

using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Java source code provider. Scans and parses .java files.
/// </summary>
public class JavaProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => JavaSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => JavaBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".java");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    /// <inheritdoc />
    public override ValueTask<object?> QueryDeferredAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        JavaTypeDeclaration.RegisterCacheFactory();
        JavaMethodDeclaration.RegisterCacheFactory();
        JavaStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new JavaSourceParser());
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(CodeCollectionBuilder.CollectDeferred(
            parsers, query, this, _ => ValueTask.FromResult(
                JavaProjectDiscovery.Discover(query.RootPath!, query.ExcludedDirectories))));
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
