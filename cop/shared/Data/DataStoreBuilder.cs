namespace Cop.Core;

/// <summary>
/// Top-level builder for creating a <see cref="DataStore"/>.
/// Manages a shared <see cref="StringHeapBuilder"/> across all tables.
/// </summary>
public class DataStoreBuilder
{
    private readonly StringHeapBuilder _heap = new();
    private readonly List<DataTableBuilder> _tables = [];

    /// <summary>
    /// Adds a table for a collection. Returns a <see cref="DataTableBuilder"/>
    /// for populating rows.
    /// </summary>
    /// <param name="collectionName">Collection name (e.g., "DiskFiles").</param>
    /// <param name="typeName">Cop type name for records (e.g., "DiskFile").</param>
    /// <param name="stride">Number of properties (slots per record).</param>
    public DataTableBuilder AddTable(string collectionName, string typeName, int stride)
    {
        var builder = new DataTableBuilder(collectionName, typeName, stride, _heap);
        _tables.Add(builder);
        return builder;
    }

    /// <summary>
    /// Finalizes the shared string heap, builds all tables, and returns the <see cref="DataStore"/>.
    /// </summary>
    public DataStore Build()
    {
        var heap = _heap.Build();
        var tables = new Dictionary<string, DataTable>(_tables.Count);
        foreach (var tb in _tables)
            tables[tb.CollectionName] = tb.Build(heap);
        return new DataStore(tables, heap);
    }
}
