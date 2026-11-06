// Adapts CLR objects (from ObjectCollections providers) to the evaluator using pre-built
// accessor function dictionaries.

using Cop.Core;
using Cop.Providers;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Adapts CLR objects (from ObjectCollections providers) to the evaluator using
/// pre-built accessor function dictionaries.
/// </summary>
public sealed class ClrObjectAdapter : IDynamicObjectAdapter
{
    private readonly Dictionary<string, Func<object, object?>> _accessors;
    private readonly Dictionary<string, Dictionary<string, Func<object, object?>>>? _allAccessors;
    private readonly Dictionary<Type, string>? _clrTypeMappings;
    private readonly string? _typeName;
    private readonly Dictionary<string, Dictionary<string,
        Func<object, CancellationToken, ValueTask<object?>>>>? _allAsyncAccessors;

    /// <inheritdoc/>
    public override string? TypeName => _typeName;
    /// <inheritdoc/>
    public override IEnumerable<string> GetFieldNames(object obj) => _accessors.Keys.Concat(
        (IEnumerable<string>?)_allAsyncAccessors?.GetValueOrDefault(_typeName ?? "")?.Keys ?? [])
        .Distinct(StringComparer.Ordinal);

    public ClrObjectAdapter(
        Dictionary<string, Func<object, object?>> accessors,
        string? typeName = null,
        Dictionary<string, Dictionary<string, Func<object, object?>>>? allAccessors = null,
        Dictionary<Type, string>? clrTypeMappings = null,
        Dictionary<string, Dictionary<string,
            Func<object, CancellationToken, ValueTask<object?>>>>? allAsyncAccessors = null)
    {
        _accessors = accessors;
        _typeName = typeName;
        _allAccessors = allAccessors;
        _clrTypeMappings = clrTypeMappings;
        _allAsyncAccessors = allAsyncAccessors;
    }

    /// <inheritdoc/>
    public override CopValue GetField(object obj, string name)
    {
        if (_allAsyncAccessors?.GetValueOrDefault(_typeName ?? "")?.ContainsKey(name) == true)
        {
            throw new InvalidOperationException($"Member '{name}' requires GetFieldAsync.");
        }
        if (_accessors.TryGetValue(name, out Func<object, object?>? accessor))
        {
            object? raw = accessor(obj);
            return MarshalWithSubCollections(raw);
        }
        return CopNull.Instance;
    }

    /// <inheritdoc/>
    public override async ValueTask<CopValue> GetFieldAsync(object obj, string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_allAsyncAccessors?.GetValueOrDefault(_typeName ?? "")?
            .TryGetValue(name, out Func<object, CancellationToken, ValueTask<object?>>? accessor)
            == true)
        {
            return MarshalWithSubCollections(
                await accessor(obj, cancellationToken).ConfigureAwait(false));
        }
        return GetField(obj, name);
    }

    private CopValue MarshalWithSubCollections(object? raw)
    {
        if (raw is null)
        {
            return CopNull.Instance;
        }

        // Check if it's a list of known CLR types
        if (raw is System.Collections.IList list && list.Count > 0 && _allAccessors is not null && _clrTypeMappings is not null)
        {
            object? firstItem = list[0];
            if (firstItem is not null && _clrTypeMappings.TryGetValue(firstItem.GetType(), out string? childTypeName) &&
                _allAccessors.TryGetValue(childTypeName, out Dictionary<string, Func<object, object?>>? childAccessors))
            {
                ClrObjectAdapter childAdapter = new ClrObjectAdapter(childAccessors,
                    childTypeName, _allAccessors, _clrTypeMappings, _allAsyncAccessors);
                List<CopValue> items = new List<CopValue>(list.Count);
                foreach (object? item in list)
                {
                    if (item is not null)
                    {
                        items.Add(new CopDynamicObject(item, childAdapter));
                    }
                }
                return new CopList(items);
            }
        }

        // Check if it's a single known CLR object
        if (_allAccessors is not null && _clrTypeMappings is not null &&
            _clrTypeMappings.TryGetValue(raw.GetType(), out string? singleTypeName) &&
            _allAccessors.TryGetValue(singleTypeName, out Dictionary<string, Func<object, object?>>? singleAccessors))
        {
            ClrObjectAdapter singleAdapter = new ClrObjectAdapter(singleAccessors,
                singleTypeName, _allAccessors, _clrTypeMappings, _allAsyncAccessors);
            return new CopDynamicObject(raw, singleAdapter);
        }

        return DataObjectAdapter.Marshal(raw);
    }

    /// <inheritdoc/>
    public override string Display(object obj)
    {
        if (_accessors.TryGetValue("Name", out Func<object, object?>? nameAccessor))
        {
            string? name = nameAccessor(obj)?.ToString();
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        if (_accessors.TryGetValue("Path", out Func<object, object?>? pathAccessor))
        {
            string? path = pathAccessor(obj)?.ToString();
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }
        return _typeName ?? obj.GetType().Name;
    }
}
