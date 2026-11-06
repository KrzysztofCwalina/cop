global using Cop.Tests.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Tests.Lang;

internal static class AsyncSourceFacts
{
    internal static async ValueTask<T> SemanticAsync<T>(T value, CancellationToken cancellationToken = default)
        where T : StatementInfo?
    {
        if (value is not null)
            await value.EnsureSemanticAnalysisAsync(cancellationToken);
        return value;
    }

    internal static async ValueTask<T> TypeNameAsync<T>(T value, CancellationToken cancellationToken = default)
        where T : StatementInfo?
    {
        if (value is not null)
            await value.EnsureTypeNameAnalysisAsync(cancellationToken);
        return value;
    }

    internal static async ValueTask<T> MembersAsync<T>(T value, CancellationToken cancellationToken = default)
        where T : TypeDeclaration?
    {
        if (value is not null)
            await value.EnsureMembersAsync(cancellationToken);
        return value;
    }

    internal static async ValueTask<T> TypeSemanticAsync<T>(T value, CancellationToken cancellationToken = default)
        where T : TypeDeclaration?
    {
        if (value is not null)
            await value.EnsureSemanticAnalysisAsync(cancellationToken);
        return value;
    }

    internal static async ValueTask<IEnumerable<MethodDeclaration>> MethodsAsync(TypeDeclaration value,
        CancellationToken cancellationToken = default)
    {
        await value.EnsureMembersAsync(cancellationToken);
        return value.Methods;
    }

    internal static async ValueTask<SourceFile> ProjectsAsync(SourceFile value,
        CancellationToken cancellationToken = default)
    {
        await value.EnsureProjectsAsync(cancellationToken);
        return value;
    }
}
