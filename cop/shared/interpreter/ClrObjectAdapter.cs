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

    public override string? TypeName => _typeName;
    public override IEnumerable<string> GetFieldNames(object obj) => _accessors.Keys;

    public ClrObjectAdapter(
        Dictionary<string, Func<object, object?>> accessors,
        string? typeName = null,
        Dictionary<string, Dictionary<string, Func<object, object?>>>? allAccessors = null,
        Dictionary<Type, string>? clrTypeMappings = null)
    {
        _accessors = accessors;
        _typeName = typeName;
        _allAccessors = allAccessors;
        _clrTypeMappings = clrTypeMappings;
    }

    public override CopValue GetField(object obj, string name)
    {
        if (_accessors.TryGetValue(name, out Func<object, object?>? accessor))
        {
            object? raw = accessor(obj);
            return MarshalWithSubCollections(raw);
        }
        return CopNull.Instance;
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
                ClrObjectAdapter childAdapter = new ClrObjectAdapter(childAccessors, childTypeName, _allAccessors, _clrTypeMappings);
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
            ClrObjectAdapter singleAdapter = new ClrObjectAdapter(singleAccessors, singleTypeName, _allAccessors, _clrTypeMappings);
            return new CopDynamicObject(raw, singleAdapter);
        }

        return DataObjectAdapter.Marshal(raw);
    }

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
