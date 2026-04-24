using System.Text;

namespace Cop.Core;

/// <summary>
/// A named collection of records stored as a flat <c>long[]</c> with a configurable stride.
/// Each record occupies <see cref="Stride"/> consecutive longs in the <see cref="Data"/> array.
/// String values are packed as (offset &lt;&lt; 32 | length) referencing the shared
/// <see cref="StringHeap"/>. Multiple DataTables in a <see cref="DataStore"/> share
/// the same string heap.
/// </summary>
public class DataTable
{
    /// <summary>
    /// Flat record data. Record i, slot j = <c>Data[i * Stride + j]</c>.
    /// </summary>
    public long[] Data { get; }

    /// <summary>
    /// Shared UTF-8 string pool. All string slots reference offsets in this heap.
    /// </summary>
    public byte[] StringHeap { get; internal set; }

    /// <summary>Number of slots per record (= property count for this type).</summary>
    public int Stride { get; }

    /// <summary>Number of records in this table.</summary>
    public int Count { get; }

    /// <summary>The cop type name for records in this table (e.g., "DiskFile").</summary>
    public string TypeName { get; }

    public DataTable(long[] data, int stride, int count, string typeName, byte[] stringHeap)
    {
        Data = data;
        Stride = stride;
        Count = count;
        TypeName = typeName;
        StringHeap = stringHeap;
    }

    /// <summary>Reads the raw long value from a slot.</summary>
    public long GetSlot(int record, int slot) => Data[record * Stride + slot];

    /// <summary>Decodes a UTF-8 string from the string heap for the given slot.</summary>
    public string GetString(int record, int slot)
    {
        long packed = Data[record * Stride + slot];
        if (packed == 0) return "";
        int offset = (int)(packed >> 32);
        int length = (int)(packed & 0xFFFFFFFF);
        return Encoding.UTF8.GetString(StringHeap, offset, length);
    }

    /// <summary>
    /// Returns the raw UTF-8 bytes for a string slot without allocating a CLR string.
    /// </summary>
    public ReadOnlySpan<byte> GetStringBytes(int record, int slot)
    {
        long packed = Data[record * Stride + slot];
        if (packed == 0) return [];
        int offset = (int)(packed >> 32);
        int length = (int)(packed & 0xFFFFFFFF);
        return StringHeap.AsSpan(offset, length);
    }

    /// <summary>Reads a boolean from a slot (0 = false, nonzero = true).</summary>
    public bool GetBool(int record, int slot) => Data[record * Stride + slot] != 0;

    /// <summary>Reads a 32-bit integer from a slot.</summary>
    public int GetInt32(int record, int slot) => (int)Data[record * Stride + slot];

    /// <summary>Reads a 64-bit integer from a slot.</summary>
    public long GetInt64(int record, int slot) => Data[record * Stride + slot];

    /// <summary>
    /// Decodes a range reference from a slot. Returns (startIndex, count) into a child table.
    /// Used for collection properties stored via <see cref="DataTableBuilder.SetRange"/>.
    /// </summary>
    public (int Start, int Count) GetRange(int record, int slot)
    {
        long packed = Data[record * Stride + slot];
        return ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
    }

    /// <summary>
    /// Decodes a single-object index reference from a slot.
    /// Returns the row index in the referenced table, or -1 for null.
    /// </summary>
    public int GetRef(int record, int slot) => (int)Data[record * Stride + slot];
}
