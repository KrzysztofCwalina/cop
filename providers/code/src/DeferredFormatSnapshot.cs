// Shares demanded format files across collections within one execution.

using Cop.Core;
using Cop.Lang;
using System.Runtime.CompilerServices;

namespace Cop.Providers;

/// <summary>One execution's demanded format files, shared across independent named collections.</summary>
public sealed class DeferredFormatSnapshot(
    Func<List<string>> discover,
    Func<string, CancellationToken, ValueTask<Dictionary<string, List<object>>>> parse)
{
    private readonly Lazy<List<string>> _paths = new(() => discover().Order(StringComparer.Ordinal).ToList());
    private readonly Dictionary<string, AsyncProviderValue<Dictionary<string, List<object>>>> _files =
        new(StringComparer.Ordinal);

    /// <summary>Identifies snapshots by analysis root and excluded directory set.</summary>
    public static string Key(ProviderQuery query) => Path.GetFullPath(query.RootPath!) + "\n" +
        string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal));

    /// <summary>
    /// Loads the requested format collection from this snapshot.
    /// </summary>
    public DeferredProviderResult Query(ProviderQuery query, ProviderSchema schema, RuntimeBindings bindings)
    {
        Dictionary<string, IAsyncEnumerable<object>> collections = new(StringComparer.Ordinal);
        foreach (ProviderCollectionSchema collection in schema.Collections)
        {
            if (query.Collection is not null && query.Collection != collection.Name)
            {
                continue;
            }

            Func<object, bool>? predicate = null;
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(collection.Name) ?? query.Filter;
            if (filter is not null && bindings.Accessors.TryGetValue(collection.ItemType, out Dictionary<string, Func<object, object?>>? accessors))
            {
                predicate = FilterCompiler.Compile(filter, accessors);
            }

            collections[collection.Name] = Enumerate(collection.Name, predicate);
        }
        return new(collections);
    }

    private async IAsyncEnumerable<object> Enumerate(
        string collection, Func<object, bool>? predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string path in _paths.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncProviderValue<Dictionary<string, List<object>>> value;
            lock (_files)
            {
                if (!_files.TryGetValue(path, out value!))
                {
                    _files[path] = value = new(token => ParseFileAsync(path, token));
                }
            }
            Dictionary<string, List<object>> parsed = await value.GetValueAsync(cancellationToken)
                .ConfigureAwait(false);
            ExecutionStatistics.RecordInputFile(path);
            if (parsed.TryGetValue(collection, out List<object>? values))
            {
                foreach (object item in values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (predicate is null || predicate(item))
                    {
                        yield return item;
                    }
                }
            }

        }
    }

    private async ValueTask<Dictionary<string, List<object>>> ParseFileAsync(
        string path, CancellationToken cancellationToken)
    {
        try { return await parse(path, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException ex)
        {
            ProviderErrors.Report($"Failed to analyze '{path}': {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderErrors.Report($"Failed to read '{path}': {ex.Message}");
        }
        return new(StringComparer.Ordinal);
    }
}
