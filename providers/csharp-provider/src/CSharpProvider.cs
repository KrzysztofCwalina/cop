using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// C# source code provider. Scans and parses .cs files using Roslyn.
/// Also provides assembly loading capability for Load('assembly.dll').
/// </summary>
public class CSharpProvider : DataProvider, ICapabilityProvider
{
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new CSharpSourceParser());
        parsers.Register(new TextFileParser());
        return CodeCollectionBuilder.CollectAndParse(parsers, query);
    }

    public void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        registry.RegisterDocumentLoader(path =>
        {
            var sourceFile = AssemblyApiReader.ReadAssembly(path);
            for (int i = 0; i < sourceFile.Types.Count; i++)
                sourceFile.Types[i] = sourceFile.Types[i] with { File = sourceFile };
            return [new Document(path, sourceFile.Language, sourceFile)];
        });
    }
}
