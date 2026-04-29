using Cop.Core;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Python source code provider. Scans and parses .py files.
/// </summary>
public class PythonProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new PythonSourceParser());
        parsers.Register(new TextFileParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from pyproject.toml/setup.py
        if (query.RootPath is not null)
        {
            var projects = PythonProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.RequestedCollections is null || query.RequestedCollections.Contains("Projects"))
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }
}
