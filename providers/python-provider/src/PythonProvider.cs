using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Python source code provider. Scans and parses .py files.
/// </summary>
public class PythonProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => PythonSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => PythonBindings.Build();

    public override object? Query(ProviderQuery query)
    {
        PythonTypeDeclaration.RegisterCacheFactory();
        var parsers = new SourceParserRegistry();
        parsers.Register(new PythonSourceParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from pyproject.toml/setup.py
        if (query.RootPath is not null)
        {
            var projects = PythonProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }
}
