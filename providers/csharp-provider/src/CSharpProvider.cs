using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// C# source code provider. Scans and parses .cs files using Roslyn.
/// Also provides csharp.Load('assembly.dll') for assembly loading.
/// </summary>
public class CSharpProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override object? Query(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new CSharpSourceParser());
        parsers.Register(new TextFileParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from .csproj files
        if (query.RootPath is not null)
        {
            var projects = CSharpProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }

    public override void RegisterCapabilities(TypeRegistry registry, string rootPath)
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
