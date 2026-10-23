using Cop.Core;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Cop language source code provider. Scans and parses .cop files,
/// exposing their structure (types, predicates, functions, imports) through
/// the shared code analysis collections, with CopFunction declarations in Methods.
/// </summary>
public class CopProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => CopSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CopBindings.Build();

    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".cop");
    public override string QueryResultType => "Codebase";

    public override object? Query(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new CopSourceParser());
        parsers.Register(new TextFileParser());
        return ApplyQueryFilters(query, CodeCollectionBuilder.CollectAndParse(parsers, query, this));
    }
}
