using Cop.Core;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Cop language source code provider. Scans and parses .cop files,
/// exposing their structure (types, predicates, functions, imports) through
/// the shared code analysis collections (Types, Statements, Lines, Files).
/// </summary>
public class CopProvider : ObjectProvider
{
    public override ObjectFormat SupportedFormats => ObjectFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new CopSourceParser());
        parsers.Register(new TextFileParser());
        return CodeCollectionBuilder.CollectAndParse(parsers, query);
    }
}
