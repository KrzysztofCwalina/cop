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
        return CodeCollectionBuilder.CollectAndParse(parsers, query);
    }
}
