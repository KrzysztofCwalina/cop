using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// C# source code provider. Scans and parses .cs files using Roslyn.
/// Also provides csharp.Load('assembly.dll') for assembly loading.
/// </summary>
public class CSharpProvider : ObjectProvider, ICapabilityProvider
{
    public override ObjectFormat SupportedFormats => ObjectFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new CSharpSourceParser());
        parsers.Register(new TextFileParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from .csproj files
        if (query.RootPath is not null)
        {
            var projects = CSharpProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.RequestedCollections is null || query.RequestedCollections.Contains("Projects"))
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }

    public void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        var extractors = CodeBindings.BuildExtractors();

        // Register csharp.Load(path) as a provider function
        registry.RegisterProviderFunction("csharp", "Load", args =>
        {
            if (args.Count < 1)
                throw new InvalidOperationException("csharp.Load requires 1 argument: csharp.Load('assembly.dll')");

            var path = args[0]?.ToString()
                ?? throw new InvalidOperationException("csharp.Load: path cannot be null");

            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"csharp.Load: file not found: {fullPath}");

            var sourceFile = AssemblyApiReader.ReadAssembly(fullPath);
            for (int i = 0; i < sourceFile.Types.Count; i++)
                sourceFile.Types[i] = sourceFile.Types[i] with { File = sourceFile };

            // Return a DataObject with lazy field resolvers for sub-collections
            var obj = new DataObject("Codebase");
            obj.WithFieldResolver(fieldName =>
            {
                if (extractors.TryGetValue(fieldName, out var extractor))
                    return extractor(sourceFile);
                return null;
            });

            return Task.FromResult<object?>(obj);
        });

        // Keep document loader registration for backward compatibility during transition
        registry.RegisterDocumentLoader(path =>
        {
            var sourceFile = AssemblyApiReader.ReadAssembly(path);
            for (int i = 0; i < sourceFile.Types.Count; i++)
                sourceFile.Types[i] = sourceFile.Types[i] with { File = sourceFile };
            return [new Document(path, sourceFile.Language, sourceFile)];
        });
    }
}
