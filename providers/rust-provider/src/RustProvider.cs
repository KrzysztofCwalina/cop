// Registers Rust parsing, project discovery, schemas, and runtime bindings.

using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Rust source code provider. Scans and parses .rs files.
/// </summary>
public class RustProvider : DataProvider
{
    /// <summary>
    /// Overrides Rust toolchain inputs; null entries remove inherited values.
    /// A null dictionary retains inherited Cargo environment behavior.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? ToolchainEnvironment { get; init; }

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => RustSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => RustBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".rs");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    /// <inheritdoc />
    public override ValueTask<object?> QueryDeferredAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        ReportAnalysisLimits(query);
        RustTypeDeclaration.RegisterCacheFactory();
        RustMethodDeclaration.RegisterCacheFactory();
        RustStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new RustSourceParser(projectRoot: query.RootPath)
        {
            ToolchainEnvironment = ToolchainEnvironment
        });
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(CodeCollectionBuilder.CollectDeferred(
            parsers, query, this, _ => ValueTask.FromResult(
                RustProjectDiscovery.Discover(query.RootPath!, query.ExcludedDirectories))));
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

    private static void ReportAnalysisLimits(ProviderQuery query)
    {
        if (RustFrontendBundle.IsAvailable)
        {
            return;
        }

        ProviderWarnings.ReportOnce("rust", query.RootPath ?? string.Empty,
            "Rust analysis is syntax-only because Cop's maintained Rust frontend is unavailable. " +
            "Install the optional cop-rust-<rid>.zip language bundle beside cop to enable it. " +
            "Using the built-in structural parser; macro expansion, name/type resolution, and cfg evaluation are unavailable. " +
            "No Cargo build, downloads, or target code execution are performed.");
    }
}
