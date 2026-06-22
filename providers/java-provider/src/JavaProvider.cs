using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Java source code provider. Scans and parses .java files.
/// </summary>
public class JavaProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => JavaSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => JavaBindings.Build();

    public override object? Query(ProviderQuery query)
    {
        // Ensure cached Java language-specific subtypes reconstruct before the cache is read.
        JavaTypeDeclaration.RegisterCacheFactory();
        JavaMethodDeclaration.RegisterCacheFactory();
        JavaStatementInfo.RegisterCacheFactory();

        var parsers = new SourceParserRegistry();
        parsers.Register(new JavaSourceParser());
        var collections = CodeCollectionBuilder.CollectAndParse(parsers, query);

        // Discover projects from pom.xml / build.gradle
        if (query.RootPath is not null)
        {
            var projects = JavaProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
                collections["Projects"] = projects.Cast<object>().ToList();
        }

        return collections;
    }
}
