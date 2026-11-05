// Shares demanded format files across collections within one execution.

using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>One execution's demanded format files, shared across independent named collections.</summary>
public sealed class DeferredFormatSnapshot(
    Func<List<string>> discover,
    Func<string, Dictionary<string, List<object>>> parse)
{
    private readonly Lazy<List<string>> _paths = new(() => discover().Order(StringComparer.Ordinal).ToList());
    private readonly Dictionary<string, Dictionary<string, List<object>>> _files = new(StringComparer.Ordinal);

    public static string Key(ProviderQuery query) => Path.GetFullPath(query.RootPath!) + "\n" +
        string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal));

    public DeferredProviderResult Query(ProviderQuery query, ProviderSchema schema, RuntimeBindings bindings)
    {
        Dictionary<string, IEnumerable<object>> collections = new Dictionary<string, IEnumerable<object>>(StringComparer.Ordinal);
        foreach (ProviderCollectionSchema collection in schema.Collections)
        {
            if (query.Collection is not null && query.Collection != collection.Name)
            {
                continue;
            }

            IEnumerable<object> values = Enumerate(collection.Name);
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(collection.Name) ?? query.Filter;
            if (filter is not null && bindings.Accessors.TryGetValue(collection.ItemType, out Dictionary<string, Func<object, object?>>? accessors))
            {
                values = values.Where(FilterCompiler.Compile(filter, accessors));
            }

            collections[collection.Name] = values;
        }
        return new(collections);
    }

    private IEnumerable<object> Enumerate(string collection)
    {
        foreach (string path in _paths.Value)
        {
            Dictionary<string, List<object>> parsed;
            lock (_files)
            {
                if (!_files.TryGetValue(path, out parsed!))
                {
                    try { parsed = parse(path); }
                    catch (InvalidDataException ex)
                    {
                        ProviderErrors.Report($"Failed to analyze '{path}': {ex.Message}");
                        parsed = new(StringComparer.Ordinal);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ProviderErrors.Report($"Failed to read '{path}': {ex.Message}");
                        parsed = new(StringComparer.Ordinal);
                    }
                    _files[path] = parsed;
                }
            }
            ExecutionStatistics.RecordInputFile(path);
            if (parsed.TryGetValue(collection, out List<object>? values))
            {
                foreach (object value in values)
                {
                    yield return value;
                }
            }
        }
    }
}
