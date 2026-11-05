// Adapts RecordView instances (from DataStore/DataTable binary format) to the evaluator.

using Cop.Core;
using Cop.Providers;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Adapts RecordView instances (from DataStore/DataTable binary format) to the evaluator.
/// Each instance is bound to a specific table schema (property-name-to-slot mapping).
/// </summary>
public sealed class RecordViewAdapter : IDynamicObjectAdapter
{
    private readonly Dictionary<string, (int Slot, string Type, bool Collection)> _schema;
    private readonly ProviderSchema? _providerSchema;
    private readonly Dictionary<string, DataTable>? _childTables;
    private readonly string? _typeName;

    public override string? TypeName => _typeName;
    public override IEnumerable<string> GetFieldNames(object obj) => _schema.Keys;

    public RecordViewAdapter(IReadOnlyList<ProviderPropertySchema> properties, Dictionary<string, DataTable>? childTables = null, string? typeName = null, ProviderSchema? providerSchema = null)
    {
        _schema = new Dictionary<string, (int, string, bool)>(properties.Count, StringComparer.Ordinal);
        for (int i = 0; i < properties.Count; i++)
        {
            _schema[properties[i].Name] = (i, properties[i].Type, properties[i].Collection);
        }

        _childTables = childTables;
        _typeName = typeName;
        _providerSchema = providerSchema;
    }

    public override CopValue GetField(object obj, string name)
    {
        if (obj is not RecordView rv)
        {
            return CopNull.Instance;
        }

        if (!_schema.TryGetValue(name, out (int Slot, string Type, bool Collection) info))
        {
            return CopNull.Instance;
        }

        (int slot, string? type, bool collection) = info;
        if (collection && _childTables is not null)
        {
            DataTable? child = _childTables.GetValueOrDefault($"{_typeName}.{name}")
                ?? _childTables.Values.FirstOrDefault(t => t.TypeName == type);
            if (child is null)
            {
                throw new CopEvaluationException($"Missing table for {_typeName}.{name}.");
            }

            (int start, int count) = rv.Table.GetRange(rv.Index, slot);
            List<ProviderPropertySchema>? properties = _providerSchema?.Types.FirstOrDefault(t => t.Name == type)?.Properties;
            RecordViewAdapter? adapter = properties is null ? null : new RecordViewAdapter(properties, _childTables, type, _providerSchema);
            return new CopLazyCollection(() => Enumerable.Range(start, count).Select(i => adapter is not null
                ? (CopValue)new CopDynamicObject(new RecordView(child, i), adapter)
                : type switch
                {
                    "string" => new CopString(child.GetString(i, 0)),
                    "int" => new CopInt(child.GetInt32(i, 0)),
                    "bool" => CopBool.Of(child.GetBool(i, 0)),
                    _ => throw new CopEvaluationException($"Unsupported child table type '{type}'.")
                }));
        }
        if (!collection && _childTables?.Values.FirstOrDefault(t => t.TypeName == type) is { } referenced
            && _providerSchema?.Types.FirstOrDefault(t => t.Name == type) is { } referencedSchema)
        {
            int index = rv.Table.GetRef(rv.Index, slot);
            return index < 0 ? CopNull.Instance : new CopDynamicObject(new RecordView(referenced, index),
                new RecordViewAdapter(referencedSchema.Properties, _childTables, type, _providerSchema));
        }
        return type switch
        {
            "bool" => CopBool.Of(rv.Table.GetBool(rv.Index, slot)),
            "int" => new CopInt(rv.Table.GetInt32(rv.Index, slot)),
            "float" => new CopNumber(BitConverter.Int64BitsToDouble(rv.Table.GetSlot(rv.Index, slot))),
            _ => new CopString(rv.Table.GetString(rv.Index, slot))
        };
    }

    public override string Display(object obj)
    {
        if (obj is not RecordView rv)
        {
            return obj.ToString() ?? "";
        }

        // Try Name, then Path, then TypeName
        if (_schema.TryGetValue("Name", out (int Slot, string Type, bool Collection) nameInfo))
        {
            string name = rv.Table.GetString(rv.Index, nameInfo.Slot);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        if (_schema.TryGetValue("Path", out (int Slot, string Type, bool Collection) pathInfo))
        {
            string path = rv.Table.GetString(rv.Index, pathInfo.Slot);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }
        return rv.Table.TypeName;
    }
}
