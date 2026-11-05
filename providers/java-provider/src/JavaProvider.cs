// Registers Java parsing, project discovery, schemas, and runtime bindings.

using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Java source code provider. Scans and parses .java files.
/// </summary>
public class JavaProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => JavaSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => JavaBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".java");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    /// <inheritdoc />
    public override object QueryDeferred(ProviderQuery query)
    {
        JavaTypeDeclaration.RegisterCacheFactory();
        JavaMethodDeclaration.RegisterCacheFactory();
        JavaStatementInfo.RegisterCacheFactory();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new JavaSourceParser());
        return CodeCollectionBuilder.CollectDeferred(parsers, query, this,
            () => JavaProjectDiscovery.Discover(query.RootPath!, query.ExcludedDirectories));
    }

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        // Ensure cached Java language-specific subtypes reconstruct before the cache is read.
        JavaTypeDeclaration.RegisterCacheFactory();
        JavaMethodDeclaration.RegisterCacheFactory();
        JavaStatementInfo.RegisterCacheFactory();

        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new JavaSourceParser());
        Dictionary<string, List<object>> collections = CodeCollectionBuilder.CollectAndParse(parsers, query, this);

        // Discover projects from pom.xml / build.gradle
        if (query.RootPath is not null)
        {
            List<ProjectInfo> projects = JavaProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories);
            if (query.Collection == null || query.Collection == "Projects")
            {
                collections["Projects"] = projects.Cast<object>().ToList();
            }
        }

        return ApplyQueryFilters(query, collections);
    }
}
