using Cop.Core;
using Cop.Providers;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Adapts DataObject instances to the clean evaluator's value system.
/// This bridges the gap between the provider layer (DataObject with dictionary fields)
/// and the evaluator (CopValue hierarchy with typed access).
///
/// The adapter lazily converts fields on access — no up-front marshaling cost.
/// </summary>
public sealed class DataObjectAdapter : IDynamicObjectAdapter
{
    public static readonly DataObjectAdapter Instance = new();

    private DataObjectAdapter() { }

    public override CopValue GetField(object obj, string name)
    {
        if (obj is not DataObject dataObj)
            return CopNull.Instance;

        var raw = dataObj.GetField(name);
        return Marshal(raw);
    }

    public override string Display(object obj)
    {
        if (obj is DataObject dataObj)
            return dataObj.GetField("Name")?.ToString()
                ?? dataObj.GetField("Path")?.ToString()
                ?? dataObj.TypeName;
        return obj.ToString() ?? "";
    }

    /// <summary>
    /// Convert a raw CLR value (from DataObject fields) to a CopValue.
    /// </summary>
    public static CopValue Marshal(object? raw)
    {
        return raw switch
        {
            null => CopNull.Instance,
            bool b => CopBool.Of(b),
            int i => new CopInt(i),
            long l => new CopInt((int)l),
            double d => new CopNumber(d),
            float f => new CopNumber(f),
            string s => new CopString(s),
            DataObject nested => new CopDynamicObject(nested, Instance),
            IReadOnlyList<DataObject> list => new CopList(
                list.Select(item => (CopValue)new CopDynamicObject(item, Instance)).ToList()),
            System.Collections.IList list => MarshalList(list),
            _ => new CopString(raw.ToString() ?? "")
        };
    }

    private static CopList MarshalList(System.Collections.IList list)
    {
        var items = new List<CopValue>(list.Count);
        foreach (var item in list)
            items.Add(Marshal(item));
        return new CopList(items);
    }
}

/// <summary>
/// Adapts RecordView instances (from DataStore/DataTable binary format) to the evaluator.
/// Each instance is bound to a specific table schema (property-name-to-slot mapping).
/// </summary>
public sealed class RecordViewAdapter : IDynamicObjectAdapter
{
    private readonly Dictionary<string, (int Slot, string Type)> _schema;
    private readonly Dictionary<string, DataTable>? _childTables;
    private readonly string? _typeName;

    public override string? TypeName => _typeName;

    public RecordViewAdapter(IReadOnlyList<ProviderPropertySchema> properties, Dictionary<string, DataTable>? childTables = null, string? typeName = null)
    {
        _schema = new Dictionary<string, (int, string)>(properties.Count, StringComparer.Ordinal);
        for (int i = 0; i < properties.Count; i++)
            _schema[properties[i].Name] = (i, properties[i].Type);
        _childTables = childTables;
        _typeName = typeName;
    }

    public override CopValue GetField(object obj, string name)
    {
        if (obj is not RecordView rv)
            return CopNull.Instance;

        if (!_schema.TryGetValue(name, out var info))
            return CopNull.Instance;

        var (slot, type) = info;
        return type switch
        {
            "bool" => CopBool.Of(rv.Table.GetBool(rv.Index, slot)),
            "int" => new CopInt(rv.Table.GetInt32(rv.Index, slot)),
            "number" => new CopNumber(BitConverter.Int64BitsToDouble(rv.Table.GetSlot(rv.Index, slot))),
            _ => new CopString(rv.Table.GetString(rv.Index, slot))
        };
    }

    public override string Display(object obj)
    {
        if (obj is not RecordView rv)
            return obj.ToString() ?? "";

        // Try Name, then Path, then TypeName
        if (_schema.TryGetValue("Name", out var nameInfo))
        {
            var name = rv.Table.GetString(rv.Index, nameInfo.Slot);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        if (_schema.TryGetValue("Path", out var pathInfo))
        {
            var path = rv.Table.GetString(rv.Index, pathInfo.Slot);
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return rv.Table.TypeName;
    }
}

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
        if (_accessors.TryGetValue(name, out var accessor))
        {
            var raw = accessor(obj);
            return MarshalWithSubCollections(raw);
        }
        return CopNull.Instance;
    }

    private CopValue MarshalWithSubCollections(object? raw)
    {
        if (raw is null) return CopNull.Instance;

        // Check if it's a list of known CLR types
        if (raw is System.Collections.IList list && list.Count > 0 && _allAccessors is not null && _clrTypeMappings is not null)
        {
            var firstItem = list[0];
            if (firstItem is not null && _clrTypeMappings.TryGetValue(firstItem.GetType(), out var childTypeName) &&
                _allAccessors.TryGetValue(childTypeName, out var childAccessors))
            {
                var childAdapter = new ClrObjectAdapter(childAccessors, childTypeName, _allAccessors, _clrTypeMappings);
                var items = new List<CopValue>(list.Count);
                foreach (var item in list)
                {
                    if (item is not null)
                        items.Add(new CopDynamicObject(item, childAdapter));
                }
                return new CopList(items);
            }
        }

        // Check if it's a single known CLR object
        if (_allAccessors is not null && _clrTypeMappings is not null &&
            _clrTypeMappings.TryGetValue(raw.GetType(), out var singleTypeName) &&
            _allAccessors.TryGetValue(singleTypeName, out var singleAccessors))
        {
            var singleAdapter = new ClrObjectAdapter(singleAccessors, singleTypeName, _allAccessors, _clrTypeMappings);
            return new CopDynamicObject(raw, singleAdapter);
        }

        return DataObjectAdapter.Marshal(raw);
    }

    public override string Display(object obj)
    {
        if (_accessors.TryGetValue("Name", out var nameAccessor))
        {
            var name = nameAccessor(obj)?.ToString();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        if (_accessors.TryGetValue("Path", out var pathAccessor))
        {
            var path = pathAccessor(obj)?.ToString();
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return _typeName ?? obj.GetType().Name;
    }
}
