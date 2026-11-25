// Answers deferred format-provider queries from per-root snapshots of discovered files.

using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Caches one <see cref="DeferredFormatSnapshot"/> per analysis root for a file-format provider and answers its
/// deferred queries. Discovered files are read as text and parsed into named collections on demand.
/// </summary>
/// <param name="language">The language recorded on each source file.</param>
/// <param name="schema">The provider schema listing the collections.</param>
/// <param name="collectFiles">Appends matching absolute file paths under a directory, honoring excluded directories.</param>
/// <param name="parseFile">Parses one file (source file, absolute path) into named collections.</param>
public sealed class DeferredFormatFiles(
    string language,
    ProviderSchema schema,
    Action<string, IReadOnlySet<string>?, List<string>> collectFiles,
    Func<SourceModel.SourceFile, string, CancellationToken, ValueTask<Dictionary<string, List<object>>>> parseFile)
{
    private readonly Dictionary<string, DeferredFormatSnapshot> _snapshots = new(StringComparer.Ordinal);

    /// <summary>
    /// Answers a deferred format query from the snapshot cached for the query's root, creating it on first use.
    /// </summary>
    /// <param name="query">The provider query.</param>
    /// <param name="bindings">The provider runtime bindings used to compile filters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deferred collections, or empty collections when the query has no root.</returns>
    public ValueTask<object?> QueryAsync(ProviderQuery query, RuntimeBindings bindings, CancellationToken cancellationToken)
    {
        if (query.RootPath is null)
        {
            return ValueTask.FromResult<object?>(new DeferredProviderResult(
                new Dictionary<string, IAsyncEnumerable<object>>()));
        }

        string key = DeferredFormatSnapshot.Key(query);
        string root = Path.GetFullPath(query.RootPath);
        if (!_snapshots.TryGetValue(key, out DeferredFormatSnapshot? snapshot))
        {
            snapshot = new(() =>
            {
                List<string> paths = new List<string>();
                collectFiles(root, query.ExcludedDirectories, paths);
                return paths;
            }, async (path, token) =>
            {
                string text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                SourceModel.SourceFile file = new(Path.GetRelativePath(root, path).Replace('\\', '/'), language, [], [], text);
                return await parseFile(file, path, token).ConfigureAwait(false);
            });
            _snapshots.Add(key, snapshot);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(snapshot.Query(query, schema, bindings));
    }
}
