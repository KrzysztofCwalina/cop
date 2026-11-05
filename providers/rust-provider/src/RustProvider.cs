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
    public override object QueryDeferred(ProviderQuery query)
    {
        ReportAnalysisLimits(query);
        RustTypeDeclaration.RegisterCacheFactory();
        RustMethodDeclaration.RegisterCacheFactory();
        RustStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new RustSourceParser(projectRoot: query.RootPath));
        return CodeCollectionBuilder.CollectDeferred(parsers, query, this,
            () => RustProjectDiscovery.Discover(query.RootPath!, query.ExcludedDirectories));
    }

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        ReportAnalysisLimits(query);
        // Ensure cached Rust types reconstruct as RustTypeDeclaration (not the plain base)
        // before the source cache is read in CollectAndParse.
        RustTypeDeclaration.RegisterCacheFactory();
        RustMethodDeclaration.RegisterCacheFactory();
        RustStatementInfo.RegisterCacheFactory();

        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new RustSourceParser(projectRoot: query.RootPath));
        Dictionary<string, List<object>> collections = CodeCollectionBuilder.CollectAndParse(parsers, query, this);

        // Discover projects from Cargo.toml
        if (query.RootPath is not null)
        {
            List<ProjectInfo> projects = RustProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
            {
                collections["Projects"] = projects.Cast<object>().ToList();
            }
        }

        return ApplyQueryFilters(query, collections);
    }

    private static void ReportAnalysisLimits(ProviderQuery query)
    {
        if (RustFrontendBundle.IsAvailable)
        {
            return;
        }

        ProviderWarnings.ReportOnce("rust", query.RootPath ?? "",
            "Rust analysis is syntax-only because Cop's maintained Rust frontend is unavailable. " +
            "Install the optional cop-rust-<rid>.zip language bundle beside cop to enable it. " +
            "Using the built-in structural parser; macro expansion, name/type resolution, and cfg evaluation are unavailable. " +
            "No Cargo build, downloads, or target code execution are performed.");
    }
}
