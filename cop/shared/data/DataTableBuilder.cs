namespace Cop.Core;

/// <summary>
/// Builder for a single <see cref="DataTable"/>. Provides typed setters for populating
/// rows and slots. String packing is handled automatically via the shared
/// <see cref="StringHeapBuilder"/>.
/// </summary>
public class DataTableBuilder
{
    private readonly string _collectionName;
    private readonly string _typeName;
    private readonly int _stride;
    private readonly StringHeapBuilder _heap;
    private long[] _data;
    private int _count;

    internal DataTableBuilder(string collectionName, string typeName, int stride, StringHeapBuilder heap)
    {
        _collectionName = collectionName;
        _typeName = typeName;
        _stride = stride;
        _heap = heap;
        _data = new long[stride * 64]; // initial capacity for 64 rows
    }

    internal string CollectionName => _collectionName;

    /// <summary>
    /// Gets the current number of rows that have been added.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Starts a new row. Returns the row index.
    /// </summary>
    public int AddRow()
    {
        int row = _count++;
        int required = _count * _stride;
        if (required > _data.Length)
        {
            int newSize = Math.Max(_data.Length * 2, required);
            Array.Resize(ref _data, newSize);
        }
        return row;
    }

    /// <summary>Packs a string and stores the packed reference in the slot.</summary>
    public void SetString(int row, int slot, string? value)
        => _data[row * _stride + slot] = _heap.PackString(value);

    /// <summary>Stores a 32-bit integer value in the slot.</summary>
    public void SetInt(int row, int slot, int value)
        => _data[row * _stride + slot] = value;

    /// <summary>Stores a 64-bit integer value in the slot.</summary>
    public void SetLong(int row, int slot, long value)
        => _data[row * _stride + slot] = value;

    /// <summary>Stores a boolean value in the slot (1 = true, 0 = false).</summary>
    public void SetBool(int row, int slot, bool value)
        => _data[row * _stride + slot] = value ? 1L : 0L;

    /// <summary>
    /// Stores a raw packed long value in the slot. Use this to reuse a packed
    /// string reference across multiple slots (e.g., Source = Path).
    /// </summary>
    public void SetSlot(int row, int slot, long packedValue)
        => _data[row * _stride + slot] = packedValue;

    /// <summary>
    /// Stores a range reference to contiguous rows in a child table.
    /// Packed as (startIndex &lt;&lt; 32 | count). Used for collection properties.
    /// </summary>
    public void SetRange(int row, int slot, int startIndex, int count)
        => _data[row * _stride + slot] = ((long)startIndex << 32) | (uint)count;

    /// <summary>
    /// Stores a range reference from a (Start, Count) tuple.
    /// Convenience overload for methods that return pre-computed ranges.
    /// </summary>
    public void SetRange(int row, int slot, (int Start, int Count) range)
        => SetRange(row, slot, range.Start, range.Count);

    /// <summary>
    /// Stores an index reference to a row in another table.
    /// Used for single-object reference properties. Use -1 for null.
    /// </summary>
    public void SetRef(int row, int slot, int index)
        => _data[row * _stride + slot] = index;

    /// <summary>
    /// Packs a string into the shared heap and returns the packed reference.
    /// Use with <see cref="SetSlot"/> when the same string value is needed in multiple slots.
    /// </summary>
    public long PackString(string? value) => _heap.PackString(value);

    /// <summary>
    /// Builds the finalized <see cref="DataTable"/>. The string heap is not yet
    /// assigned — it will be wired in by <see cref="DataStoreBuilder.Build"/>.
    /// </summary>
    internal DataTable Build(byte[] stringHeap)
    {
        // Trim to exact size
        var data = new long[_count * _stride];
        Array.Copy(_data, data, data.Length);
        return new DataTable(data, _stride, _count, _typeName, stringHeap);
    }
}
