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
    public override object QueryDeferred(ProviderQuery query)
    {
        PythonTypeDeclaration.RegisterCacheFactory();
        PythonMethodDeclaration.RegisterCacheFactory();
        PythonStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new PythonSourceParser());
        return CodeCollectionBuilder.CollectDeferred(parsers, query, this,
            () => PythonProjectDiscovery.Discover(query.RootPath!, query.ExcludedDirectories));
    }

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        PythonTypeDeclaration.RegisterCacheFactory();
        PythonMethodDeclaration.RegisterCacheFactory();
        PythonStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new PythonSourceParser());
        Dictionary<string, List<object>> collections = CodeCollectionBuilder.CollectAndParse(parsers, query, this);

        // Discover projects from pyproject.toml/setup.py
        if (query.RootPath is not null)
        {
            List<ProjectInfo> projects = PythonProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
            {
                collections["Projects"] = projects.Cast<object>().ToList();
            }
        }

        return ApplyQueryFilters(query, collections);
    }
}
