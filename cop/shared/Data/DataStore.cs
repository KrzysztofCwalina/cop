namespace Cop.Core;

/// <summary>
/// An in-memory database returned by providers via <see cref="DataProvider.QueryData"/>.
/// Contains named <see cref="DataTable"/> instances (one per collection) that all
/// share a single UTF-8 <see cref="StringHeap"/>.
/// </summary>
public class DataStore
{
    /// <summary>
    /// Tables keyed by collection name (e.g., "DiskFiles", "Folders").
    /// </summary>
    public Dictionary<string, DataTable> Tables { get; }

    /// <summary>
    /// Shared UTF-8 string pool referenced by all tables.
    /// </summary>
    public byte[] StringHeap { get; }

    public DataStore(Dictionary<string, DataTable> tables, byte[] stringHeap)
    {
        Tables = tables;
        StringHeap = stringHeap;
    }
}
