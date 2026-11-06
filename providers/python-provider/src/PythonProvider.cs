// Registers Python parsing, project discovery, schemas, and runtime bindings.

using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Python source code provider. Scans and parses .py files.
/// </summary>
public class PythonProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => PythonSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => PythonBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".py", ".pyi");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    /// <inheritdoc />
    public override ValueTask<object?> QueryDeferredAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        PythonTypeDeclaration.RegisterCacheFactory();
        PythonMethodDeclaration.RegisterCacheFactory();
        PythonStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new PythonSourceParser());
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(CodeCollectionBuilder.CollectDeferred(
            parsers, query, this, token => PythonProjectDiscovery.DiscoverAsync(
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
